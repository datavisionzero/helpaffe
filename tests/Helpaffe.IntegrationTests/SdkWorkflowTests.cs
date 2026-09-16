using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpaffe.Sdk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class SdkWorkflowTests : IAsyncLifetime
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
    public async Task Sdk_completes_the_end_user_workflow_against_the_real_product_API()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
                builder.UseSetting("Bootstrap:Name", "Admin");
                builder.UseSetting("Bootstrap:Email", "admin@example.test");
                builder.UseSetting("Bootstrap:Password", AdminPassword);
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
        var created = await sdk.CreateTicketAsync(
            new CreateProductTicket(
                "stable-user-42",
                "SDK User",
                "sdk-user@example.test",
                "SDK integration question",
                "Can the SDK submit this?",
                JsonSerializer.SerializeToElement(new { product_version = "3.1.0", page = "/account" })),
            "sdk-create-42",
            TestContext.Current.CancellationToken);

        var page = await sdk.ListTicketsAsync("stable-user-42", cancellationToken: TestContext.Current.CancellationToken);
        var read = await sdk.GetTicketAsync(created.Summary.Number, "stable-user-42", TestContext.Current.CancellationToken);
        var replied = await sdk.AddReplyAsync(
            read.Summary.Number,
            "stable-user-42",
            "One more detail from the product.",
            read.Summary.Version,
            "sdk-reply-42",
            TestContext.Current.CancellationToken);

        Assert.Equal(created.Summary.Number, Assert.Single(page.Items).Number);
        Assert.Equal("3.1.0", read.Context?.GetProperty("product_version").GetString());
        Assert.Equal(2, replied.Summary.Version);
        Assert.Equal(2, replied.Conversation.Count);
        var hidden = await Assert.ThrowsAsync<HelpaffeApiException>(() => sdk.GetTicketAsync(
            created.Summary.Number,
            "different-user",
            TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("not-found", hidden.Code);
    }
}
