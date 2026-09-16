using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class ProductApiTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-for-tests";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-tests")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Product_tickets_are_isolated_by_project_and_external_user_and_expose_only_public_history()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory);
        var firstProject = await CreateProject(admin, "FIRST", "First product");
        var secondProject = await CreateProject(admin, "SECOND", "Second product");
        using var firstProduct = BearerClient(factory, await CreateProductKey(admin, firstProject));
        using var secondProduct = BearerClient(factory, await CreateProductKey(admin, secondProject));
        var firstRequest = new
        {
            external_user_id = "customer-7",
            name = "Ada User",
            email = "ada@old.example.test",
            subject = "Settings are blank",
            message = "I cannot open settings.",
            context = new { version = "2.4.1", page = "/settings", environment = new { browser = "Firefox" } },
        };

        var created = await SendProductJson(firstProduct, HttpMethod.Post, "/api/product/tickets", firstRequest, "create-first");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
        var createdDocument = await Read(created);
        var number = createdDocument.GetProperty("summary").GetProperty("number").GetString()!;
        Assert.Equal("2.4.1", createdDocument.GetProperty("context").GetProperty("version").GetString());

        var replay = await SendProductJson(firstProduct, HttpMethod.Post, "/api/product/tickets", firstRequest, "create-first");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(number, (await Read(replay)).GetProperty("summary").GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendProductJson(firstProduct, HttpMethod.Post, "/api/product/tickets", new
            {
                external_user_id = "customer-7",
                name = "Ada User",
                email = "ada@old.example.test",
                subject = "Different",
                message = "Different",
            }, "create-first")).StatusCode);

        var secondCreated = await SendProductJson(firstProduct, HttpMethod.Post, "/api/product/tickets", new
        {
            external_user_id = "customer-7",
            name = "Ada User",
            email = "ada@new.example.test",
            subject = "Another request",
            message = "This is a second request.",
        }, "create-second");
        Assert.Equal(HttpStatusCode.Created, secondCreated.StatusCode);
        var secondNumber = (await Read(secondCreated)).GetProperty("summary").GetProperty("number").GetString();
        Assert.NotEqual(number, secondNumber);

        var list = await Read(await firstProduct.GetAsync(
            "/api/product/tickets?external_user_id=customer-7",
            TestContext.Current.CancellationToken));
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
        Assert.Empty((await Read(await firstProduct.GetAsync(
            "/api/product/tickets?external_user_id=another-customer",
            TestContext.Current.CancellationToken))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Read(await secondProduct.GetAsync(
            "/api/product/tickets?external_user_id=customer-7",
            TestContext.Current.CancellationToken))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await firstProduct.GetAsync(
                $"/api/product/tickets/{number}?external_user_id=another-customer",
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await secondProduct.GetAsync(
                $"/api/product/tickets/{number}?external_user_id=customer-7",
                TestContext.Current.CancellationToken)).StatusCode);

        var crossProject = await SendProductJson(secondProduct, HttpMethod.Post, "/api/product/tickets", new
        {
            external_user_id = "customer-7",
            name = "Ada User",
            email = "ada@second.example.test",
            subject = "Second product request",
            message = "This belongs to the second product.",
        }, "create-cross-project");
        Assert.Equal(HttpStatusCode.Created, crossProject.StatusCode);
        Assert.Single((await Read(await secondProduct.GetAsync(
            "/api/product/tickets?external_user_id=customer-7",
            TestContext.Current.CancellationToken))).GetProperty("items").EnumerateArray());

        var supportRead = await admin.GetAsync($"/api/backoffice/tickets/{number}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, supportRead.StatusCode);
        var supportDocument = await Read(supportRead);
        Assert.Equal("/settings", supportDocument.GetProperty("context").GetProperty("page").GetString());
        var version = supportDocument.GetProperty("summary").GetProperty("version").GetInt32();
        var note = await SendTicketJson(admin, HttpMethod.Post, $"/api/backoffice/tickets/{number}/notes", new
        {
            message = "This must remain internal.",
        }, version, "internal-note");
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        version = (await Read(note)).GetProperty("summary").GetProperty("version").GetInt32();
        var supportReply = await SendTicketJson(admin, HttpMethod.Post, $"/api/backoffice/tickets/{number}/replies", new
        {
            message = "Please try the suggested fix.",
            status = "resolved",
        }, version, "support-reply");
        Assert.Equal(HttpStatusCode.OK, supportReply.StatusCode);
        version = (await Read(supportReply)).GetProperty("summary").GetProperty("version").GetInt32();

        var productRead = await firstProduct.GetAsync(
            $"/api/product/tickets/{number}?external_user_id=customer-7",
            TestContext.Current.CancellationToken);
        var productDocument = await Read(productRead);
        Assert.Equal("ada@old.example.test", productDocument.GetProperty("requester").GetProperty("email").GetString());
        Assert.Equal("resolved", productDocument.GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal(2, productDocument.GetProperty("conversation").GetArrayLength());
        Assert.All(productDocument.GetProperty("conversation").EnumerateArray(), entry =>
            Assert.Contains(entry.GetProperty("kind").GetString(), new[] { "customer_message", "public_reply" }));
        Assert.DoesNotContain("internal", productDocument.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(productDocument.TryGetProperty("support_instructions", out _));

        var customerReply = await SendProductJson(
            firstProduct,
            HttpMethod.Post,
            $"/api/product/tickets/{number}/replies",
            new { external_user_id = "customer-7", message = "The issue still happens." },
            "customer-reply",
            version);
        Assert.Equal(HttpStatusCode.OK, customerReply.StatusCode);
        var customerReplyDocument = await Read(customerReply);
        Assert.Equal("open", customerReplyDocument.GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal(version + 1, customerReplyDocument.GetProperty("summary").GetProperty("version").GetInt32());
        Assert.Equal(3, customerReplyDocument.GetProperty("conversation").GetArrayLength());
        var customerReplay = await SendProductJson(
            firstProduct,
            HttpMethod.Post,
            $"/api/product/tickets/{number}/replies",
            new { external_user_id = "customer-7", message = "The issue still happens." },
            "customer-reply",
            version);
        Assert.Equal(HttpStatusCode.OK, customerReplay.StatusCode);
        Assert.Equal(version + 1, (await Read(customerReplay)).GetProperty("summary").GetProperty("version").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await database.Tickets.Include(value => value.Conversation)
            .SingleAsync(value => value.Number == number, TestContext.Current.CancellationToken);
        Assert.Equal(firstProject, stored.ProjectId);
        Assert.Equal("customer-7", stored.RequesterExternalId);
        Assert.Equal(TicketStatus.Open, stored.Status);
        Assert.Single(stored.Conversation, value => value.Kind is ConversationEntryKind.CustomerMessage && value.Sequence > 1);
    }

    [Fact]
    public async Task Product_ticket_input_rejects_missing_identity_unknown_upload_fields_and_oversized_or_non_object_context()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory);
        var project = await CreateProject(admin, "VALIDATE", "Validation product");
        using var product = BearerClient(factory, await CreateProductKey(admin, project));
        var valid = new
        {
            external_user_id = "customer-1",
            name = "Customer",
            email = "customer@example.test",
            subject = "Question",
            message = "Please help.",
        };

        Assert.Equal(HttpStatusCode.BadRequest,
            (await product.PostAsJsonAsync("/api/product/tickets", valid, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendProductJson(product, HttpMethod.Post, "/api/product/tickets", new
            {
                external_user_id = "",
                name = "Customer",
                email = "customer@example.test",
                subject = "Question",
                message = "Please help.",
            }, "missing-identity")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendProductJson(product, HttpMethod.Post, "/api/product/tickets", new
            {
                valid.external_user_id,
                valid.name,
                valid.email,
                valid.subject,
                valid.message,
                attachments = new[] { "forbidden.txt" },
            }, "upload-field")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendProductJson(product, HttpMethod.Post, "/api/product/tickets", new
            {
                valid.external_user_id,
                valid.name,
                valid.email,
                valid.subject,
                valid.message,
                context = new[] { "not", "an", "object" },
            }, "array-context")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendProductJson(product, HttpMethod.Post, "/api/product/tickets", new
            {
                valid.external_user_id,
                valid.name,
                valid.email,
                valid.subject,
                valid.message,
                context = new { data = new string('x', 17 * 1024) },
            }, "large-context")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await product.GetAsync("/api/product/tickets", TestContext.Current.CancellationToken)).StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
            builder.UseSetting("Bootstrap:Name", "Admin");
            builder.UseSetting("Bootstrap:Email", "admin@example.test");
            builder.UseSetting("Bootstrap:Password", AdminPassword);
        });

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/backoffice/session", new
        {
            email = "admin@example.test",
            password = AdminPassword,
        }, TestContext.Current.CancellationToken);
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

    private static async Task<string> CreateProductKey(HttpClient admin, Guid projectId)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/backoffice/projects/{projectId}/product-keys",
            new { name = "Integration" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> SendProductJson(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        string idempotencyKey,
        int? version = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> SendTicketJson(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        int version,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<JsonElement> Read(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
