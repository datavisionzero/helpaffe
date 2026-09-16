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

}
