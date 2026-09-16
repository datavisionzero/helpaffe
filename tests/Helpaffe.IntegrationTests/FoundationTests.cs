using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class FoundationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-tests")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Empty_database_is_migrated_and_readiness_is_healthy()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:Database",
                _postgres.GetConnectionString()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var migrations = await database.Database
            .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(migrations, migration => migration.EndsWith("_InitialFoundation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Roles_deactivation_and_project_access_are_enforced_on_each_request()
    {
        const string adminPassword = "admin-password-for-tests";
        const string supportPassword = "support-password-for-tests";
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
                builder.UseSetting("Bootstrap:Name", "Admin");
                builder.UseSetting("Bootstrap:Email", "admin@example.test");
                builder.UseSetting("Bootstrap:Password", adminPassword);
            });
        using var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/backoffice/session", new
        {
            email = "admin@example.test",
            password = adminPassword,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        admin.DefaultRequestHeaders.Add("X-Helpaffe-CSRF", "1");

        var projectResponse = await admin.PostAsJsonAsync("/api/backoffice/projects", new
        {
            key = "PRODUCT",
            name = "Product",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        var userResponse = await admin.PostAsJsonAsync("/api/backoffice/users", new
        {
            name = "Support",
            email = "support@example.test",
            password = supportPassword,
            role = "support",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, userResponse.StatusCode);
        var user = await userResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var userId = user.GetProperty("id").GetGuid();
        var projectId = project.GetProperty("id").GetGuid();

        using var support = factory.CreateClient();
        var supportLogin = await support.PostAsJsonAsync("/api/backoffice/session", new
        {
            email = "support@example.test",
            password = supportPassword,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, supportLogin.StatusCode);
        support.DefaultRequestHeaders.Add("X-Helpaffe-CSRF", "1");

        var forbidden = await support.PostAsJsonAsync("/api/backoffice/users", new
        {
            name = "Nope", email = "nope@example.test", password = "not-allowed-password", role = "support",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Empty(await support.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? []);

        var grant = await admin.PutAsync(
            $"/api/backoffice/users/{userId}/projects/{projectId}",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        Assert.Single(await support.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? []);

        var revoke = await admin.DeleteAsync(
            $"/api/backoffice/users/{userId}/projects/{projectId}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Empty(await support.GetFromJsonAsync<JsonElement[]>("/api/backoffice/projects", TestContext.Current.CancellationToken) ?? []);

        var deactivate = new HttpRequestMessage(HttpMethod.Patch, $"/api/backoffice/users/{userId}")
        {
            Content = JsonContent.Create(new { is_active = false }),
        };
        var deactivated = await admin.SendAsync(deactivate, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await support.GetAsync("/api/backoffice/me", TestContext.Current.CancellationToken)).StatusCode);
    }
}
