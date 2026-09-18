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
    private readonly string _attachmentRoot = Path.Combine(Path.GetTempPath(), $"helpaffe-attachments-{Guid.NewGuid():N}");

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_attachmentRoot)) Directory.Delete(_attachmentRoot, recursive: true);
    }

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

    [Fact]
    public async Task Attachments_are_validated_persisted_and_downloaded_only_in_their_visibility_scope()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory);
        var project = await CreateProject(admin, "FILES", "Files product");
        var otherProject = await CreateProject(admin, "OTHERFILES", "Other files product");
        using var product = BearerClient(factory, await CreateProductKey(admin, project));
        using var otherProduct = BearerClient(factory, await CreateProductKey(admin, otherProject));

        using var createForm = new MultipartFormDataContent();
        createForm.Add(new StringContent("customer-attachment"), "external_user_id");
        createForm.Add(new StringContent("Ada User"), "name");
        createForm.Add(new StringContent("ada@example.test"), "email");
        createForm.Add(new StringContent("Screenshot attached"), "subject");
        createForm.Add(new StringContent("The page is broken."), "message");
        var pngBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4 };
        var png = new ByteArrayContent(pngBytes);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        createForm.Add(png, "files", "screen.png");
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/product/tickets") { Content = createForm };
        createRequest.Headers.Add("Idempotency-Key", "create-with-file");

        var created = await product.SendAsync(createRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var document = await Read(created);
        var number = document.GetProperty("summary").GetProperty("number").GetString()!;
        var initial = Assert.Single(document.GetProperty("conversation").EnumerateArray());
        var publicAttachment = Assert.Single(initial.GetProperty("attachments").EnumerateArray());
        var publicAttachmentId = publicAttachment.GetProperty("id").GetGuid();
        Assert.Equal("image/png", publicAttachment.GetProperty("media_type").GetString());

        var productDownload = await product.GetAsync(
            $"/api/product/tickets/{number}/attachments/{publicAttachmentId}?external_user_id=customer-attachment",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, productDownload.StatusCode);
        Assert.Equal(pngBytes, await productDownload.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, (await otherProduct.GetAsync(
            $"/api/product/tickets/{number}/attachments/{publicAttachmentId}?external_user_id=customer-attachment",
            TestContext.Current.CancellationToken)).StatusCode);

        var version = document.GetProperty("summary").GetProperty("version").GetInt32();
        using var noteForm = new MultipartFormDataContent();
        noteForm.Add(new StringContent("Private diagnostic output."), "message");
        var text = new ByteArrayContent("secret trace"u8.ToArray());
        text.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        noteForm.Add(text, "files", "trace.txt");
        using var noteRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/backoffice/tickets/{number}/notes") { Content = noteForm };
        noteRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        noteRequest.Headers.Add("Idempotency-Key", "note-with-file");
        var noted = await admin.SendAsync(noteRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, noted.StatusCode);
        var noteDocument = await Read(noted);
        var note = Assert.Single(noteDocument.GetProperty("conversation").EnumerateArray(),
            value => value.GetProperty("kind").GetString() == "internal_note");
        var internalAttachmentId = Assert.Single(note.GetProperty("attachments").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(
            $"/api/backoffice/tickets/{number}/attachments/{internalAttachmentId}",
            TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await product.GetAsync(
            $"/api/product/tickets/{number}/attachments/{internalAttachmentId}?external_user_id=customer-attachment",
            TestContext.Current.CancellationToken)).StatusCode);

        using var invalidForm = new MultipartFormDataContent();
        invalidForm.Add(new StringContent("customer-attachment"), "external_user_id");
        invalidForm.Add(new StringContent("Another message"), "message");
        var disguised = new ByteArrayContent("not a png"u8.ToArray());
        disguised.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        invalidForm.Add(disguised, "files", "fake.png");
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/product/tickets/{number}/replies") { Content = invalidForm };
        invalidRequest.Headers.TryAddWithoutValidation("If-Match",
            $"\"{noteDocument.GetProperty("summary").GetProperty("version").GetInt32()}\"");
        invalidRequest.Headers.Add("Idempotency-Key", "invalid-file");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await product.SendAsync(invalidRequest, TestContext.Current.CancellationToken)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await database.TicketAttachments.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, Directory.EnumerateFiles(_attachmentRoot, "*", SearchOption.AllDirectories).Count());
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
            builder.UseSetting("Bootstrap:Name", "Admin");
            builder.UseSetting("Bootstrap:Email", "admin@example.test");
            builder.UseSetting("Bootstrap:Password", AdminPassword);
            builder.UseSetting("Attachments:RootPath", _attachmentRoot);
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
