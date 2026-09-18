extern alias ProductExample;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpaffe.Sdk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using ProductExampleApplication = ProductExample::ProductExampleApplication;

namespace Helpaffe.IntegrationTests;

public sealed class SdkWorkflowTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-for-tests";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-tests")
        .Build();
    private readonly string _attachmentRoot = Path.Combine(Path.GetTempPath(), $"helpaffe-sdk-attachments-{Guid.NewGuid():N}");

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_attachmentRoot)) Directory.Delete(_attachmentRoot, recursive: true);
    }

    [Fact]
    public async Task Sdk_completes_the_end_user_workflow_against_the_real_product_API()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
                builder.UseSetting("Bootstrap:Name", "Admin");
                builder.UseSetting("Bootstrap:Email", "admin@example.test");
                builder.UseSetting("Bootstrap:Password", AdminPassword);
                builder.UseSetting("Attachments:RootPath", _attachmentRoot);
            });
        using var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/backoffice/session", new
        {
            email = "admin@example.test",
            password = AdminPassword,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        admin.DefaultRequestHeaders.Add("X-Helpaffe-CSRF", "1");
        var projectResponse = await admin.PostAsJsonAsync("/api/backoffice/projects", new
        {
            key = "SDK",
            name = "SDK product",
        }, TestContext.Current.CancellationToken);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/backoffice/projects/{project.GetProperty("id").GetGuid()}/product-keys",
            new { name = "SDK test" },
            TestContext.Current.CancellationToken);
        var key = await keyResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        using var transport = factory.CreateClient();
        var sdk = new HelpaffeProductClient(transport, transport.BaseAddress!, key.GetProperty("token").GetString()!);
        using var initialFile = new MemoryStream("SDK evidence"u8.ToArray());
        var created = await sdk.CreateTicketAsync(
            new CreateProductTicket(
                "stable-user-42",
                "SDK User",
                "sdk-user@example.test",
                "SDK integration question",
                "Can the SDK submit this?",
                JsonSerializer.SerializeToElement(new { product_version = "3.1.0", page = "/account" })),
            "sdk-create-42",
            [new ProductAttachmentUpload("evidence.txt", "text/plain", initialFile)],
            TestContext.Current.CancellationToken);

        var page = await sdk.ListTicketsAsync("stable-user-42", cancellationToken: TestContext.Current.CancellationToken);
        var read = await sdk.GetTicketAsync(created.Summary.Number, "stable-user-42", TestContext.Current.CancellationToken);
        var initialAttachment = Assert.Single(read.Conversation[0].Attachments);
        await using (var download = await sdk.DownloadAttachmentAsync(
            read.Summary.Number, "stable-user-42", initialAttachment.Id, TestContext.Current.CancellationToken))
        {
            using var reader = new StreamReader(download.Content);
            Assert.Equal("SDK evidence", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        using var replyFile = new MemoryStream("{\"reproduced\":true}"u8.ToArray());
        var replied = await sdk.AddReplyAsync(
            read.Summary.Number,
            "stable-user-42",
            "One more detail from the product.",
            read.Summary.Version,
            "sdk-reply-42",
            [new ProductAttachmentUpload("diagnostic.json", "application/json", replyFile)],
            TestContext.Current.CancellationToken);

        Assert.Equal(created.Summary.Number, Assert.Single(page.Items).Number);
        Assert.Equal("3.1.0", read.Context?.GetProperty("product_version").GetString());
        Assert.Equal(2, replied.Summary.Version);
        Assert.Equal(2, replied.Conversation.Count);
        Assert.Equal("diagnostic.json", Assert.Single(replied.Conversation[1].Attachments).FileName);
        var hidden = await Assert.ThrowsAsync<HelpaffeApiException>(() => sdk.GetTicketAsync(
            created.Summary.Number,
            "different-user",
            TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("not-found", hidden.Code);
        var hiddenAttachment = await Assert.ThrowsAsync<HelpaffeApiException>(() => sdk.DownloadAttachmentAsync(
            created.Summary.Number,
            "different-user",
            initialAttachment.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, hiddenAttachment.StatusCode);
    }

    [Fact]
    public async Task Product_example_proxies_attachment_uploads_and_downloads_without_exposing_the_key()
    {
        await using var apiFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
                builder.UseSetting("Bootstrap:Name", "Admin");
                builder.UseSetting("Bootstrap:Email", "admin@example.test");
                builder.UseSetting("Bootstrap:Password", AdminPassword);
                builder.UseSetting("Attachments:RootPath", _attachmentRoot);
            });
        using var admin = apiFactory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/backoffice/session", new
        {
            email = "admin@example.test",
            password = AdminPassword,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        admin.DefaultRequestHeaders.Add("X-Helpaffe-CSRF", "1");
        var projectResponse = await admin.PostAsJsonAsync("/api/backoffice/projects", new
        {
            key = "EXAMPLE",
            name = "Product example",
        }, TestContext.Current.CancellationToken);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var keyResponse = await admin.PostAsJsonAsync(
            $"/api/backoffice/projects/{project.GetProperty("id").GetGuid()}/product-keys",
            new { name = "Example test" },
            TestContext.Current.CancellationToken);
        var key = await keyResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var token = key.GetProperty("token").GetString()!;

        await using var exampleFactory = new WebApplicationFactory<ProductExampleApplication>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Helpaffe:BaseAddress", "http://localhost");
                builder.UseSetting("Helpaffe:ProductApiKey", "hfp_replaced_for_test");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<HelpaffeProductClient>();
                    services.AddSingleton(new HelpaffeProductClient(
                        apiFactory.CreateClient(),
                        apiFactory.Server.BaseAddress,
                        token));
                });
            });
        using var product = exampleFactory.CreateClient();
        product.DefaultRequestHeaders.Add("X-Example-User-Id", "example-user-7");
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Example attachment"), "subject" },
            { new StringContent("Please inspect the attached log."), "message" },
            { new StringContent("example-create-7"), "requestId" },
            { new StringContent("1.2.3"), "productVersion" },
            { new StringContent("/support"), "page" },
        };
        var file = new ByteArrayContent("browser-safe evidence"u8.ToArray());
        file.Headers.ContentType = new("text/plain");
        form.Add(file, "files", "evidence.txt");

        var createdResponse = await product.PostAsync(
            "/support/tickets",
            form,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var number = created.GetProperty("summary").GetProperty("number").GetString()!;
        var firstEntry = created.GetProperty("conversation")[0];
        Assert.Equal("customer_message", firstEntry.GetProperty("kind").GetString());
        var attachment = firstEntry.GetProperty("attachments")[0];
        Assert.Equal("evidence.txt", attachment.GetProperty("file_name").GetString());
        var attachmentId = attachment.GetProperty("id").GetGuid();

        var download = await product.GetAsync(
            $"/support/tickets/{number}/attachments/{attachmentId}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(
            "attachment; filename*=UTF-8''evidence.txt",
            download.Content.Headers.ContentDisposition!.ToString(),
            ignoreCase: true);
        Assert.Equal("browser-safe evidence", await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("hfp_", await createdResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        product.DefaultRequestHeaders.Remove("X-Example-User-Id");
        product.DefaultRequestHeaders.Add("X-Example-User-Id", "different-user");
        var hidden = await product.GetAsync(
            $"/support/tickets/{number}/attachments/{attachmentId}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }
}
