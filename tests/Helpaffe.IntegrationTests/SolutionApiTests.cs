using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class SolutionApiTests : IAsyncLifetime
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
    public async Task Solution_articles_are_project_scoped_searchable_and_concurrency_safe()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory, "admin@example.test", AdminPassword);
        var firstProject = await CreateProject(admin, "FIRST", "First product");
        var secondProject = await CreateProject(admin, "SECOND", "Second product");
        var supportId = await CreateSupportUser(admin);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsync($"/api/backoffice/users/{supportId}/projects/{firstProject}", null,
                TestContext.Current.CancellationToken)).StatusCode);
        using var supportAgent = await CreateAgent(admin, factory, supportId);

        var invalid = await PostJson(supportAgent, $"/api/backoffice/projects/{firstProject}/solutions", new
        {
            key = "invalid key",
            title = "Invalid",
            markdown = "Body",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var created = await PostJson(supportAgent, $"/api/backoffice/projects/{firstProject}/solutions", new
        {
            key = "postgres-restart",
            title = "Restart PostgreSQL safely",
            markdown = "Use the tested PostgreSQL restart playbook.",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
        var createdDocument = await Read(created);
        Assert.Equal("postgres-restart", createdDocument.GetProperty("key").GetString());
        Assert.Equal(1, createdDocument.GetProperty("version").GetInt32());

        var duplicate = await PostJson(supportAgent, $"/api/backoffice/projects/{firstProject}/solutions", new
        {
            key = "POSTGRES-RESTART",
            title = "Duplicate",
            markdown = "Duplicate body",
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await supportAgent.GetAsync($"/api/backoffice/projects/{secondProject}/solutions",
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostJson(supportAgent, $"/api/backoffice/projects/{secondProject}/solutions", new
            {
                key = "hidden",
                title = "Hidden",
                markdown = "Must not be stored",
            })).StatusCode);

        var secondCreated = await PostJson(supportAgent, $"/api/backoffice/projects/{firstProject}/solutions", new
        {
            key = "browser-cache",
            title = "Clear browser cache",
            markdown = "Clear site data and reload.",
        });
        Assert.Equal(HttpStatusCode.Created, secondCreated.StatusCode);
        var firstPage = await Read(await supportAgent.GetAsync(
            $"/api/backoffice/projects/{firstProject}/solutions?limit=1", TestContext.Current.CancellationToken));
        Assert.Single(firstPage.GetProperty("items").EnumerateArray());
        var cursor = Uri.EscapeDataString(firstPage.GetProperty("next_cursor").GetString()!);
        var nextPage = await Read(await supportAgent.GetAsync(
            $"/api/backoffice/projects/{firstProject}/solutions?limit=1&cursor={cursor}", TestContext.Current.CancellationToken));
        Assert.Single(nextPage.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await supportAgent.GetAsync(
                $"/api/backoffice/projects/{firstProject}/solutions?limit=1&search=postgres&cursor={cursor}",
                TestContext.Current.CancellationToken)).StatusCode);

        var search = await Read(await supportAgent.GetAsync(
            $"/api/backoffice/projects/{firstProject}/solutions?search=PostgreSQL",
            TestContext.Current.CancellationToken));
        var searchResult = Assert.Single(search.GetProperty("items").EnumerateArray());
        Assert.Equal("postgres-restart", searchResult.GetProperty("key").GetString());

        var get = await supportAgent.GetAsync(
            $"/api/backoffice/projects/{firstProject}/solutions/postgres-restart",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"1\"", get.Headers.ETag?.Tag);

        var withoutVersion = await PutJson(supportAgent,
            $"/api/backoffice/projects/{firstProject}/solutions/postgres-restart",
            new { title = "Updated", markdown = "Updated body" });
        Assert.Equal((HttpStatusCode)428, withoutVersion.StatusCode);

        var updated = await PutJson(supportAgent,
            $"/api/backoffice/projects/{firstProject}/solutions/postgres-restart",
            new { title = "Restart PostgreSQL", markdown = "Use the current tested playbook." }, 1);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("\"2\"", updated.Headers.ETag?.Tag);
        Assert.Equal(2, (await Read(updated)).GetProperty("version").GetInt32());

        var stale = await PutJson(supportAgent,
            $"/api/backoffice/projects/{firstProject}/solutions/postgres-restart",
            new { title = "Stale", markdown = "Must not win." }, 1);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(2, (await Read(stale)).GetProperty("current_version").GetInt32());

        var staleDelete = await Delete(supportAgent,
            $"/api/backoffice/projects/{firstProject}/solutions/postgres-restart", 1);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleDelete.StatusCode);
        var deleted = await Delete(supportAgent,
            $"/api/backoffice/projects/{firstProject}/solutions/postgres-restart", 2);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await supportAgent.GetAsync(
                $"/api/backoffice/projects/{firstProject}/solutions/postgres-restart",
                TestContext.Current.CancellationToken)).StatusCode);
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
        var response = await client.PostAsJsonAsync("/api/backoffice/session", new { email, password },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("X-Helpaffe-CSRF", "1");
        return client;
    }

    private static async Task<Guid> CreateProject(HttpClient admin, string key, string name)
    {
        var response = await PostJson(admin, "/api/backoffice/projects", new { key, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateSupportUser(HttpClient admin)
    {
        var response = await PostJson(admin, "/api/backoffice/users", new
        {
            name = "Support",
            email = "support@example.test",
            password = SupportPassword,
            role = "support",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpClient> CreateAgent(
        HttpClient admin,
        WebApplicationFactory<Program> factory,
        Guid userId)
    {
        var response = await PostJson(admin, "/api/backoffice/agents", new
        {
            name = "Support agent",
            user_id = userId,
            all_projects = true,
            project_ids = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await Read(response)).GetProperty("token").GetString());
        return client;
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PutJson(HttpClient client, string path, object body, int? version = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> Delete(HttpClient client, string path, int version)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<JsonElement> Read(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
