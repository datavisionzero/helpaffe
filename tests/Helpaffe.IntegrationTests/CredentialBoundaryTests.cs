using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class CredentialBoundaryTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-for-tests";
    private const string SupportPassword = "support-password-for-tests";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-tests")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Agent_scope_tracks_its_user_and_human_only_boundaries_are_enforced()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory, "admin@example.test", AdminPassword);
        var firstProject = await CreateProject(admin, "FIRST", "First product");
        var secondProject = await CreateProject(admin, "SECOND", "Second product");
        var adminAgentResponse = await PostJson(admin, "/api/backoffice/agents", new
        {
            name = "Administrator agent", all_projects = true, project_ids = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Created, adminAgentResponse.StatusCode);
        using var adminAgent = BearerClient(factory, (await Read(adminAgentResponse)).GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.Created,
            (await PostJson(adminAgent, "/api/backoffice/projects", new { key = "AGENT", name = "Created by agent" })).StatusCode);
        Assert.Equal(3, (await adminAgent.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? []).Length);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await adminAgent.GetAsync("/api/backoffice/users", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostJson(adminAgent, "/api/backoffice/agents", new { name = "Forbidden", all_projects = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostJson(adminAgent, $"/api/backoffice/projects/{firstProject}/product-keys", new { name = "Forbidden" })).StatusCode);
        var support = await CreateSupport(admin);
        await Grant(admin, support, firstProject);

        using var supportBrowser = await SignIn(factory, "support@example.test", SupportPassword);
        var invalidSubset = await PostJson(supportBrowser, "/api/backoffice/agents", new
        {
            name = "Too broad", all_projects = false, project_ids = new[] { secondProject },
        });
        Assert.Equal(HttpStatusCode.Forbidden, invalidSubset.StatusCode);

        var scopedAgentResponse = await PostJson(supportBrowser, "/api/backoffice/agents", new
        {
            name = "First project only", all_projects = false, project_ids = new[] { firstProject },
        });
        Assert.Equal(HttpStatusCode.Created, scopedAgentResponse.StatusCode);
        var scopedAgentToken = (await Read(scopedAgentResponse)).GetProperty("token").GetString()!;
        using var scopedAgent = BearerClient(factory, scopedAgentToken);

        var createdAgent = await PostJson(supportBrowser, "/api/backoffice/agents", new
        {
            name = "Support agent",
            all_projects = true,
            project_ids = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Created, createdAgent.StatusCode);
        var agentDocument = await Read(createdAgent);
        var agentId = agentDocument.GetProperty("credential").GetProperty("id").GetGuid();
        var agentToken = agentDocument.GetProperty("token").GetString()!;

        using var agent = BearerClient(factory, agentToken);
        Assert.Single(await agent.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? []);

        await Grant(admin, support, secondProject);
        Assert.Equal(2, (await agent.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? []).Length);
        Assert.Single(await scopedAgent.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? []);
        await RevokeGrant(admin, support, firstProject);
        var remaining = await agent.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? [];
        Assert.Single(remaining);
        Assert.Equal(secondProject, remaining[0].GetProperty("id").GetGuid());
        Assert.Empty(await scopedAgent.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? []);

        var agentCreatesAgent = await PostJson(agent, "/api/backoffice/agents", new
        {
            name = "Forbidden", all_projects = true, project_ids = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, agentCreatesAgent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync("/api/backoffice/users", TestContext.Current.CancellationToken)).StatusCode);

        var supportCreatesProductKey = await PostJson(
            supportBrowser,
            $"/api/backoffice/projects/{secondProject}/product-keys",
            new { name = "Forbidden" });
        Assert.Equal(HttpStatusCode.Forbidden, supportCreatesProductKey.StatusCode);

        var revoked = await supportBrowser.DeleteAsync($"/api/backoffice/agents/{agentId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await agent.GetAsync("/api/backoffice/projects", TestContext.Current.CancellationToken)).StatusCode);

        var deactivate = new HttpRequestMessage(HttpMethod.Patch, $"/api/backoffice/users/{support}")
        {
            Content = JsonContent.Create(new { is_active = false }),
        };
        var deactivated = await admin.SendAsync(deactivate, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await scopedAgent.GetAsync("/api/backoffice/projects", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Product_keys_are_project_bound_independently_revocable_and_rejected_by_backoffice()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory, "admin@example.test", AdminPassword);
        var project = await CreateProject(admin, "PRODUCT", "Product");

        var first = await CreateProductKey(admin, project, "Primary");
        var second = await CreateProductKey(admin, project, "Rotation");
        using var firstProduct = BearerClient(factory, first.Token);
        using var secondProduct = BearerClient(factory, second.Token);

        var firstProject = await firstProduct.GetAsync("/api/product/project", TestContext.Current.CancellationToken);
        var secondProject = await secondProduct.GetAsync("/api/product/project", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstProject.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondProject.StatusCode);
        Assert.Equal(project, (await Read(firstProject)).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await firstProduct.GetAsync("/api/backoffice/projects", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await firstProduct.GetAsync("/api/backoffice/tickets?search=internal", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await admin.GetAsync("/api/product/project", TestContext.Current.CancellationToken)).StatusCode);

        var revoked = await admin.DeleteAsync($"/api/backoffice/product-keys/{first.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await firstProduct.GetAsync("/api/product/project", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await secondProduct.GetAsync("/api/product/project", TestContext.Current.CancellationToken)).StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
            builder.UseSetting("Bootstrap:Name", "Admin");
            builder.UseSetting("Bootstrap:Email", "admin@example.test");
            builder.UseSetting("Bootstrap:Password", AdminPassword);
        });

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/backoffice/session",
            new { email, password },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
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
        var response = await PostJson(admin, "/api/backoffice/projects", new { key, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateSupport(HttpClient admin)
    {
        var response = await PostJson(admin, "/api/backoffice/users", new
        {
            name = "Support", email = "support@example.test", password = SupportPassword, role = "support",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task Grant(HttpClient admin, Guid userId, Guid projectId)
    {
        var response = await admin.PutAsync(
            $"/api/backoffice/users/{userId}/projects/{projectId}",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task RevokeGrant(HttpClient admin, Guid userId, Guid projectId)
    {
        var response = await admin.DeleteAsync(
            $"/api/backoffice/users/{userId}/projects/{projectId}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<(Guid Id, string Token)> CreateProductKey(HttpClient admin, Guid projectId, string name)
    {
        var response = await PostJson(admin, $"/api/backoffice/projects/{projectId}/product-keys", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var document = await Read(response);
        return (document.GetProperty("credential").GetProperty("id").GetGuid(), document.GetProperty("token").GetString()!);
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);

    private static Task<JsonElement> Read(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
