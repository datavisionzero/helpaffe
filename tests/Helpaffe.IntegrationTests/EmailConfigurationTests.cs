using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class EmailConfigurationTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-for-tests";
    private const string SupportPassword = "support-password-for-tests";
    private static readonly string EncryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-tests")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Administrators_manage_isolated_settings_templates_preview_and_test_delivery_without_revealing_secrets()
    {
        await using var smtp = new CapturingSmtpServer();
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory, "admin@example.test", AdminPassword);
        var firstProject = await CreateProject(admin, "FIRSTMAIL", "First mail product");
        var secondProject = await CreateProject(admin, "SECONDMAIL", "Second mail product");
        var supportId = await CreateSupport(admin);
        await admin.PutAsync($"/api/backoffice/users/{supportId}/projects/{firstProject}", null, TestContext.Current.CancellationToken);
        using var support = await SignIn(factory, "support@example.test", SupportPassword);
        var agentResponse = await admin.PostAsJsonAsync("/api/backoffice/agents", new
        {
            name = "Mail administrator",
            all_projects = true,
            project_ids = Array.Empty<Guid>(),
        }, TestContext.Current.CancellationToken);
        using var adminAgent = BearerClient(factory, (await Read(agentResponse)).GetProperty("token").GetString()!);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await support.GetAsync($"/api/backoffice/projects/{firstProject}/email-settings", TestContext.Current.CancellationToken)).StatusCode);
        var defaults = await Read(await admin.GetAsync(
            $"/api/backoffice/projects/{firstProject}/email-settings",
            TestContext.Current.CancellationToken));
        Assert.Equal("en", defaults.GetProperty("language").GetString());
        Assert.False(defaults.GetProperty("smtp").GetProperty("password_configured").GetBoolean());

        var configured = await PutSettings(adminAgent, firstProject, smtp.Port, "First Brand", "smtp-password-do-not-return", "first-support@example.test");
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        var configuredText = await configured.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("smtp-password-do-not-return", configuredText, StringComparison.Ordinal);
        Assert.DoesNotContain("cipher", configuredText, StringComparison.OrdinalIgnoreCase);
        var configuredDocument = JsonDocument.Parse(configuredText).RootElement;
        Assert.True(configuredDocument.GetProperty("smtp").GetProperty("password_configured").GetBoolean());
        Assert.Equal("en", configuredDocument.GetProperty("language").GetString());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutSettings(adminAgent, secondProject, smtp.Port, "Second Brand", null, "second-support@example.test", language: "de")).StatusCode);
        var secondConfigured = await PutSettings(adminAgent, secondProject, smtp.Port, "Second Brand", null, "second-support@example.test", username: null);
        Assert.Equal(HttpStatusCode.OK, secondConfigured.StatusCode);
        var firstRead = await Read(await adminAgent.GetAsync(
            $"/api/backoffice/projects/{firstProject}/email-settings",
            TestContext.Current.CancellationToken));
        var secondRead = await Read(await adminAgent.GetAsync(
            $"/api/backoffice/projects/{secondProject}/email-settings",
            TestContext.Current.CancellationToken));
        Assert.Equal("First Brand", firstRead.GetProperty("branding").GetProperty("name").GetString());
        Assert.Equal("Second Brand", secondRead.GetProperty("branding").GetProperty("name").GetString());
        Assert.Equal("first-support@example.test", firstRead.GetProperty("support_recipients")[0].GetString());
        Assert.Equal("second-support@example.test", secondRead.GetProperty("support_recipients")[0].GetString());

        var templates = await Read(await adminAgent.GetAsync(
            $"/api/backoffice/projects/{firstProject}/email-templates",
            TestContext.Current.CancellationToken));
        Assert.Equal(5, templates.GetArrayLength());
        Assert.All(templates.EnumerateArray(), value =>
        {
            Assert.False(value.GetProperty("is_customized").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("text_body").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("html_body").GetString()));
        });
        var invalidTemplate = await adminAgent.PutAsJsonAsync(
            $"/api/backoffice/projects/{firstProject}/email-templates/public_reply_customer",
            new { subject = "{{unknown}}", text_body = "Text", html_body = "<p>HTML</p>" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidTemplate.StatusCode);
        var savedTemplate = await adminAgent.PutAsJsonAsync(
            $"/api/backoffice/projects/{firstProject}/email-templates/public_reply_customer",
            new
            {
                subject = "A reply for {{ticket_number}}",
                text_body = "Hello {{customer_name}}: {{message}} {{ticket_url}}",
                html_body = "<h1>{{brand_name}}</h1><p>{{message}}</p><a href=\"{{ticket_url}}\">Open</a>",
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, savedTemplate.StatusCode);
        Assert.True((await Read(savedTemplate)).GetProperty("is_customized").GetBoolean());

        var preview = await adminAgent.PostAsJsonAsync(
            $"/api/backoffice/projects/{firstProject}/email-templates/public_reply_customer/preview",
            new
            {
                customer_name = "Avery",
                ticket_number = "HLP-99",
                ticket_subject = "Preview",
                message = "<not-html>",
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewDocument = await Read(preview);
        Assert.Equal("A reply for HLP-99", previewDocument.GetProperty("subject").GetString());
        Assert.Contains("&lt;not-html&gt;", previewDocument.GetProperty("html_body").GetString());
        Assert.Contains("HLP-99", previewDocument.GetProperty("text_body").GetString());

        // Preserve the encrypted secret while disabling authentication for the local capture server.
        var unauthenticatedSmtp = await PutSettings(adminAgent, firstProject, smtp.Port, "First Brand", null, "first-support@example.test", username: null);
        Assert.Equal(HttpStatusCode.OK, unauthenticatedSmtp.StatusCode);
        var testSend = await adminAgent.PostAsJsonAsync(
            $"/api/backoffice/projects/{firstProject}/email/test",
            new
            {
                recipient = "test-recipient@example.test",
                template_type = "public_reply_customer",
                sample = new { customer_name = "Test Customer", ticket_number = "HLP-TEST", message = "SMTP test message" },
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, testSend.StatusCode);
        var captured = await smtp.Message.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("Subject: A reply for HLP-TEST", captured, StringComparison.Ordinal);
        Assert.Contains("To: test-recipient@example.test", captured, StringComparison.Ordinal);
        Assert.Contains("multipart/alternative", captured, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text/plain", captured, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text/html", captured, StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await database.ProjectEmailSettings.SingleAsync(value => value.ProjectId == firstProject, TestContext.Current.CancellationToken);
        Assert.NotNull(stored.SmtpPasswordCiphertext);
        Assert.DoesNotContain("smtp-password-do-not-return", stored.SmtpPasswordCiphertext, StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
            builder.UseSetting("Bootstrap:Name", "Admin");
            builder.UseSetting("Bootstrap:Email", "admin@example.test");
            builder.UseSetting("Bootstrap:Password", AdminPassword);
            builder.UseSetting("Secrets:EncryptionKey", EncryptionKey);
        });

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/backoffice/session",
            new { email, password },
            TestContext.Current.CancellationToken);
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

    private static async Task<Guid> CreateProject(HttpClient admin, string key, string name)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/backoffice/projects",
            new { key, name },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateSupport(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/backoffice/users", new
        {
            name = "Support",
            email = "support@example.test",
            password = SupportPassword,
            role = "support",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PutSettings(
        HttpClient client,
        Guid projectId,
        int smtpPort,
        string brandName,
        string? password,
        string supportRecipient,
        string language = "en",
        string? username = "smtp-user") =>
        client.PutAsJsonAsync($"/api/backoffice/projects/{projectId}/email-settings", new
        {
            language,
            smtp = new { host = "127.0.0.1", port = smtpPort, use_tls = false, username, password },
            sender = new { name = brandName, email = "sender@example.test" },
            support_recipients = new[] { supportRecipient },
            branding = new { name = brandName, logo_url = "https://cdn.example.test/logo.png", color = "#336699" },
            ticket_links = new
            {
                customer = "https://product.example.test/support/{{ticket_number}}",
                backoffice = "https://support.example.test/tickets/{{ticket_number}}",
            },
        }, TestContext.Current.CancellationToken);

    private static Task<JsonElement> Read(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private sealed class CapturingSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serve;
        private readonly TaskCompletionSource<string> _message = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CapturingSmtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync();
        }

        public int Port { get; }
        public Task<string> Message => _message.Task;

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
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
                        _message.TrySetResult(message.ToString());
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
            catch (OperationCanceledException)
            {
                _message.TrySetCanceled(_cancellation.Token);
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
