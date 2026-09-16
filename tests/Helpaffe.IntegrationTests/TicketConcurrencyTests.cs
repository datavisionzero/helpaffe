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

public sealed class TicketConcurrencyTests : IAsyncLifetime
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
    public async Task Parallel_agents_claim_distinct_tickets_in_priority_and_wait_order()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory);
        var project = await CreateProject(admin);
        var support = await CreateSupport(admin);
        await Grant(admin, support, project);
        using var firstAgent = await CreateAgent(admin, factory, support, "First agent");
        using var secondAgent = await CreateAgent(admin, factory, support, "Second agent");
        await SeedClaimTickets(factory, project);

        var firstCall = Acquire(firstAgent, project, "first-claim");
        var secondCall = Acquire(secondAgent, project, "second-claim");
        var responses = await Task.WhenAll(firstCall, secondCall);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var documents = await Task.WhenAll(responses.Select(Read));
        var numbers = documents.Select(value => value.GetProperty("summary").GetProperty("number").GetString()).ToHashSet();
        Assert.Equal(2, numbers.Count);
        Assert.Contains("HLP-U1", numbers);
        Assert.Contains("HLP-U2", numbers);

        var third = await Acquire(firstAgent, project, "third-claim");
        Assert.Equal("HLP-N1", (await Read(third)).GetProperty("summary").GetProperty("number").GetString());
        var none = await Acquire(secondAgent, project, "no-ticket");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.EndsWith("/no-ticket", (await Read(none)).GetProperty("type").GetString());

        var replay = await Acquire(firstAgent, project, "third-claim");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("HLP-N1", (await Read(replay)).GetProperty("summary").GetProperty("number").GetString());
        var mismatch = await Acquire(firstAgent, null, "third-claim");
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await database.Tickets.AsNoTracking().OrderBy(value => value.Number).ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(stored, value =>
        {
            Assert.Equal(TicketStatus.InProgress, value.Status);
            Assert.Equal(support, value.AssigneeUserId);
        });
    }

    [Fact]
    public async Task Parallel_writes_with_one_version_yield_one_success_and_one_stale_response()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory);
        var project = await CreateProject(admin);
        var support = await CreateSupport(admin);
        await Grant(admin, support, project);
        using var agent = await CreateAgent(admin, factory, support, "Writer");
        await SeedTicket(factory, project, "HLP-C1", TicketPriority.Normal, DateTimeOffset.UtcNow.AddMinutes(-1));

        var statusChange = TicketPatch(agent, "HLP-C1", new { status = "resolved" }, 1);
        var priorityChange = TicketPatch(agent, "HLP-C1", new { priority = "urgent" }, 1);
        var responses = await Task.WhenAll(statusChange, priorityChange);

        Assert.Single(responses, value => value.StatusCode == HttpStatusCode.OK);
        var stale = Assert.Single(responses, value => value.StatusCode == HttpStatusCode.PreconditionFailed);
        Assert.Equal(2, (await Read(stale)).GetProperty("current_version").GetInt32());
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
        var response = await client.PostAsJsonAsync(
            "/api/backoffice/session",
            new { email = "admin@example.test", password = AdminPassword },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("X-Helpaffe-CSRF", "1");
        return client;
    }

    private static async Task<Guid> CreateProject(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/backoffice/projects",
            new { key = "PRODUCT", name = "Product" },
            TestContext.Current.CancellationToken);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateSupport(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/backoffice/users",
            new { name = "Support", email = "support@example.test", password = "support-password-for-tests", role = "support" },
            TestContext.Current.CancellationToken);
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

    private static async Task<HttpClient> CreateAgent(
        HttpClient admin,
        WebApplicationFactory<Program> factory,
        Guid userId,
        string name)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/backoffice/agents",
            new { name, user_id = userId, all_projects = true, project_ids = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);
        var token = (await Read(response)).GetProperty("token").GetString()!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task SeedClaimTickets(WebApplicationFactory<Program> factory, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        await SeedTicket(factory, projectId, "HLP-N1", TicketPriority.Normal, now.AddMinutes(-30));
        await SeedTicket(factory, projectId, "HLP-U2", TicketPriority.Urgent, now.AddMinutes(-5));
        await SeedTicket(factory, projectId, "HLP-U1", TicketPriority.Urgent, now.AddMinutes(-10));
    }

    private static async Task SeedTicket(
        WebApplicationFactory<Program> factory,
        Guid projectId,
        string number,
        TicketPriority priority,
        DateTimeOffset waitingSince)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var ticket = Ticket.Create(
            Guid.NewGuid(), number, projectId, $"Subject {number}", number, "Requester", "requester@example.test", "Help", waitingSince);
        if (priority is TicketPriority.Urgent)
        {
            var admin = await database.Users.SingleAsync(value => value.Role == Helpaffe.Domain.Identity.UserRole.Administrator, TestContext.Current.CancellationToken);
            ticket.ChangePriority(priority, admin.Id, null, waitingSince.AddSeconds(1));
        }
        database.Tickets.Add(ticket);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> Acquire(HttpClient client, Guid? projectId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/backoffice/tickets/next")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { project_id = projectId }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> TicketPatch(HttpClient client, string number, object body, int version)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/backoffice/tickets/{number}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<JsonElement> Read(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
