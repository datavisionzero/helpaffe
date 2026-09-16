using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Helpaffe.Api.Hosting;
using Helpaffe.Infrastructure.Notifications;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class NotificationDeliveryTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-for-tests";
    private const string CustomerEmail = "customer@example.test";
    private static readonly string EncryptionKey = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-tests")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Durable_outbox_routes_recipients_retries_failures_and_manual_recovery()
    {
        await using var smtp = new CapturingSmtpServer();
        Guid projectId;
        Guid assigneeId;
        string productToken;
        string ticketNumber;

        // Create and commit the ticket, then dispose the complete application host before dispatching.
        await using (var firstFactory = CreateFactory())
        {
            using var admin = await SignIn(firstFactory);
            projectId = await CreateProject(admin);
            assigneeId = await CreateSupport(admin, "Assignee", "assignee@example.test");
            await Grant(admin, assigneeId, projectId);
            await ConfigureEmail(admin, projectId, smtp.Port);
            productToken = await CreateProductKey(admin, projectId);
            using var product = BearerClient(firstFactory, productToken);
            var created = await ProductRequest(product, HttpMethod.Post, "/api/product/tickets", new
            {
                external_user_id = "customer-7",
                name = "Avery Customer",
                email = CustomerEmail,
                subject = "Durable notifications",
                message = "Please help me.",
            }, "create-ticket");
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            ticketNumber = (await Read(created)).GetProperty("summary").GetProperty("number").GetString()!;
            Assert.Equal(2, await DeliveryCount(firstFactory, ticketNumber));
        }

        await using var factory = CreateFactory();
        using var adminAfterRestart = await SignIn(factory);
        using var productAfterRestart = BearerClient(factory, productToken);
        var dispatcher = factory.Services.GetRequiredService<NotificationDispatcher>();

        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        var initialRecipients = new[]
        {
            Recipient(await smtp.ReadMessageAsync()),
            Recipient(await smtp.ReadMessageAsync()),
            Recipient(await smtp.ReadMessageAsync()),
        };
        Assert.Equal(
            new[] { CustomerEmail, "support-one@example.test", "support-two@example.test" },
            initialRecipients.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, await DeliveryCount(factory, ticketNumber));
        Assert.All(await Deliveries(factory, ticketNumber), value => Assert.Equal(NotificationOutbox.Submitted, value.Status));

        var detail = await Read(await adminAfterRestart.GetAsync(
            $"/api/backoffice/tickets/{ticketNumber}", TestContext.Current.CancellationToken));
        var version = detail.GetProperty("summary").GetProperty("version").GetInt32();
        var deliveryCount = detail.GetProperty("notifications").GetArrayLength();

        var note = await TicketRequest(adminAfterRestart, HttpMethod.Post,
            $"/api/backoffice/tickets/{ticketNumber}/notes", new { message = "Internal diagnosis." }, version, "note");
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        version = (await Read(note)).GetProperty("summary").GetProperty("version").GetInt32();
        var fields = await TicketRequest(adminAfterRestart, HttpMethod.Patch,
            $"/api/backoffice/tickets/{ticketNumber}", new { status = "in_progress", priority = "urgent" }, version);
        Assert.Equal(HttpStatusCode.OK, fields.StatusCode);
        version = (await Read(fields)).GetProperty("summary").GetProperty("version").GetInt32();
        Assert.Equal(deliveryCount, await DeliveryCount(factory, ticketNumber));

        var publicReply = await TicketRequest(adminAfterRestart, HttpMethod.Post,
            $"/api/backoffice/tickets/{ticketNumber}/replies",
            new { message = "We are investigating.", status = "waiting_for_customer" }, version, "public-reply");
        Assert.Equal(HttpStatusCode.OK, publicReply.StatusCode);
        version = (await Read(publicReply)).GetProperty("summary").GetProperty("version").GetInt32();
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CustomerEmail, Recipient(await smtp.ReadMessageAsync()));

        var unassignedCustomerReply = await ProductRequest(productAfterRestart, HttpMethod.Post,
            $"/api/product/tickets/{ticketNumber}/replies",
            new { external_user_id = "customer-7", message = "More detail before assignment." },
            "unassigned-customer-reply", version);
        Assert.Equal(HttpStatusCode.OK, unassignedCustomerReply.StatusCode);
        version = (await Read(unassignedCustomerReply)).GetProperty("summary").GetProperty("version").GetInt32();
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            new[] { "support-one@example.test", "support-two@example.test" },
            new[] { Recipient(await smtp.ReadMessageAsync()), Recipient(await smtp.ReadMessageAsync()) }
                .Order(StringComparer.Ordinal).ToArray());

        var assigned = await TicketRequest(adminAfterRestart, HttpMethod.Patch,
            $"/api/backoffice/tickets/{ticketNumber}", new { assignee_id = assigneeId }, version);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        version = (await Read(assigned)).GetProperty("summary").GetProperty("version").GetInt32();
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal("assignee@example.test", Recipient(await smtp.ReadMessageAsync()));

        var customerReply = await ProductRequest(productAfterRestart, HttpMethod.Post,
            $"/api/product/tickets/{ticketNumber}/replies",
            new { external_user_id = "customer-7", message = "It still fails." }, "customer-reply", version);
        Assert.Equal(HttpStatusCode.OK, customerReply.StatusCode);
        version = (await Read(customerReply)).GetProperty("summary").GetProperty("version").GetInt32();
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal("assignee@example.test", Recipient(await smtp.ReadMessageAsync()));

        var unavailablePort = UnavailableTcpPort();
        await ConfigureEmail(adminAfterRestart, projectId, unavailablePort);
        var outageReply = await TicketRequest(adminAfterRestart, HttpMethod.Post,
            $"/api/backoffice/tickets/{ticketNumber}/replies",
            new { message = "This delivery will be retried.", status = "waiting_for_customer" }, version, "outage-reply");
        Assert.Equal(HttpStatusCode.OK, outageReply.StatusCode);
        var outageDocument = await Read(outageReply);
        version = outageDocument.GetProperty("summary").GetProperty("version").GetInt32();
        var failedId = outageDocument.GetProperty("notifications").EnumerateArray()
            .Last(value => value.GetProperty("type").GetString() == "public_reply_customer")
            .GetProperty("id").GetGuid();

        await AssertTransientAttempt(factory, dispatcher, failedId, 1, TimeSpan.FromMinutes(1));
        await MakeDue(factory, failedId);
        await AssertTransientAttempt(factory, dispatcher, failedId, 2, TimeSpan.FromMinutes(5));
        await MakeDue(factory, failedId);
        await AssertTransientAttempt(factory, dispatcher, failedId, 3, TimeSpan.FromMinutes(30));
        await MakeDue(factory, failedId);
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        var failed = await Delivery(factory, failedId);
        Assert.Equal(NotificationOutbox.Failed, failed.Status);
        Assert.Equal(4, failed.AttemptCount);
        Assert.Null(failed.NextAttemptAt);
        Assert.Equal("SMTP delivery failed.", failed.LastError);

        var visibleFailure = await Read(await adminAfterRestart.GetAsync(
            $"/api/backoffice/tickets/{ticketNumber}", TestContext.Current.CancellationToken));
        var visibleNotification = visibleFailure.GetProperty("notifications").EnumerateArray()
            .Single(value => value.GetProperty("id").GetGuid() == failedId);
        Assert.Equal("failed", visibleNotification.GetProperty("status").GetString());
        Assert.Equal("SMTP delivery failed.", visibleNotification.GetProperty("last_error").GetString());
        var conversationCount = visibleFailure.GetProperty("conversation").GetArrayLength();

        await ConfigureEmail(adminAfterRestart, projectId, smtp.Port);
        var retry = await Retry(adminAfterRestart, ticketNumber, failedId, "manual-retry");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("pending", (await Read(retry)).GetProperty("status").GetString());
        var replay = await Retry(adminAfterRestart, ticketNumber, failedId, "manual-retry");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var afterRetry = await Read(await adminAfterRestart.GetAsync(
            $"/api/backoffice/tickets/{ticketNumber}", TestContext.Current.CancellationToken));
        Assert.Equal(version, afterRetry.GetProperty("summary").GetProperty("version").GetInt32());
        Assert.Equal(conversationCount, afterRetry.GetProperty("conversation").GetArrayLength());

        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CustomerEmail, Recipient(await smtp.ReadMessageAsync()));
        var recovered = await Delivery(factory, failedId);
        Assert.Equal(NotificationOutbox.Submitted, recovered.Status);
        Assert.Equal(1, recovered.AttemptCount);
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
            builder.UseSetting("Bootstrap:Name", "Admin");
            builder.UseSetting("Bootstrap:Email", "admin@example.test");
            builder.UseSetting("Bootstrap:Password", AdminPassword);
            builder.UseSetting("Secrets:EncryptionKey", EncryptionKey);
            builder.UseSetting("Notifications:WorkerEnabled", "false");
        });

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/backoffice/session",
            new { email = "admin@example.test", password = AdminPassword }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("X-Helpaffe-CSRF", "1");
        return client;
    }

    private static HttpClient BearerClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> CreateProject(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/backoffice/projects",
            new { key = "NOTIFY", name = "Notification product" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateSupport(HttpClient admin, string name, string email)
    {
        var response = await admin.PostAsJsonAsync("/api/backoffice/users", new
        {
            name,
            email,
            password = "support-password-for-tests",
            role = "support",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task Grant(HttpClient admin, Guid userId, Guid projectId)
    {
        var response = await admin.PutAsync($"/api/backoffice/users/{userId}/projects/{projectId}", null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string> CreateProductKey(HttpClient admin, Guid projectId)
    {
        var response = await admin.PostAsJsonAsync($"/api/backoffice/projects/{projectId}/product-keys",
            new { name = "Notification test" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("token").GetString()!;
    }

    private static async Task ConfigureEmail(HttpClient admin, Guid projectId, int port)
    {
        var response = await admin.PutAsJsonAsync($"/api/backoffice/projects/{projectId}/email-settings", new
        {
            language = "en",
            smtp = new { host = "127.0.0.1", port, use_tls = false, username = (string?)null, password = (string?)null },
            sender = new { name = "Notification product", email = "sender@example.test" },
            support_recipients = new[] { "support-one@example.test", "support-two@example.test" },
            branding = new { name = "Notification product", logo_url = (string?)null, color = "#336699" },
            ticket_links = new
            {
                customer = "https://product.example.test/support/{{ticket_number}}",
                backoffice = "https://support.example.test/tickets/{{ticket_number}}",
            },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> TicketRequest(
        HttpClient client, HttpMethod method, string path, object body, int version, string? key = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> ProductRequest(
        HttpClient client, HttpMethod method, string path, object body, string key, int? version = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> Retry(HttpClient client, string number, Guid id, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/backoffice/tickets/{number}/notifications/{id}/retry");
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task AssertTransientAttempt(
        WebApplicationFactory<Program> factory,
        NotificationDispatcher dispatcher,
        Guid id,
        int attempt,
        TimeSpan delay)
    {
        var before = DateTimeOffset.UtcNow;
        Assert.True(await dispatcher.ProcessOneAsync(TestContext.Current.CancellationToken));
        var delivery = await Delivery(factory, id);
        Assert.Equal(NotificationOutbox.Pending, delivery.Status);
        Assert.Equal(attempt, delivery.AttemptCount);
        Assert.NotNull(delivery.NextAttemptAt);
        Assert.InRange(delivery.NextAttemptAt!.Value, before.Add(delay).AddSeconds(-2), DateTimeOffset.UtcNow.Add(delay).AddSeconds(2));
    }

    private static async Task MakeDue(WebApplicationFactory<Program> factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        await database.NotificationDeliveries.Where(value => value.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)),
                TestContext.Current.CancellationToken);
    }

    private static async Task<int> DeliveryCount(WebApplicationFactory<Program> factory, string number) =>
        (await Deliveries(factory, number)).Count;

    private static async Task<List<NotificationDeliveryRecord>> Deliveries(
        WebApplicationFactory<Program> factory, string number)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var ticketId = await database.Tickets.Where(value => value.Number == number)
            .Select(value => value.Id).SingleAsync(TestContext.Current.CancellationToken);
        return await database.NotificationDeliveries.AsNoTracking()
            .Where(value => value.TicketId == ticketId)
            .OrderBy(value => value.CreatedAt)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<NotificationDeliveryRecord> Delivery(WebApplicationFactory<Program> factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await database.NotificationDeliveries.AsNoTracking().SingleAsync(value => value.Id == id,
            TestContext.Current.CancellationToken);
    }

    private static int UnavailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Recipient(string message) => message.Split('\n')
        .Single(line => line.StartsWith("To: ", StringComparison.Ordinal))[4..].Trim();

    private static Task<JsonElement> Read(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private sealed class CapturingSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        private readonly Task _serve;

        public CapturingSmtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync();
        }

        public int Port { get; }

        public Task<string> ReadMessageAsync() =>
            _messages.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    await HandleAsync(client);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                _messages.Writer.TryComplete();
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
            await using (var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            })
            {
                await writer.WriteLineAsync("220 localhost helpaffe test SMTP");
                while (await reader.ReadLineAsync(_cancellation.Token) is { } line)
                {
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250 8BITMIME");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var message = new StringBuilder();
                        while (await reader.ReadLineAsync(_cancellation.Token) is { } data && data != ".")
                            message.AppendLine(data.StartsWith("..", StringComparison.Ordinal) ? data[1..] : data);
                        await _messages.Writer.WriteAsync(message.ToString(), _cancellation.Token);
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 bye");
                        break;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 ok");
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await _serve; } catch (OperationCanceledException) { }
            _cancellation.Dispose();
        }
    }
}
