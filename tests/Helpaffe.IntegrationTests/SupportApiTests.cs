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

public sealed class SupportApiTests : IAsyncLifetime
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
    public async Task Ticket_context_mutations_filters_and_project_scope_are_enforced_for_agents()
    {
        await using var factory = CreateFactory();
        using var admin = await SignIn(factory, "admin@example.test", AdminPassword);
        var firstProject = await CreateProject(admin, "FIRST", "First product");
        var secondProject = await CreateProject(admin, "SECOND", "Second product");
        var support = await CreateUser(admin, "Support", "support@example.test", SupportPassword);
        var otherSupport = await CreateUser(admin, "Other", "other@example.test", "other-password-for-tests");
        await Grant(admin, support, firstProject);
        var supportAgent = await CreateAgent(admin, factory, support, "Support agent");
        var adminAgent = await CreateAgent(admin, factory, null, "Administrator agent");
        Assert.Equal(HttpStatusCode.OK,
            (await PatchJson(adminAgent, $"/api/backoffice/projects/{firstProject}", new { name = "First product renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PatchJson(supportAgent, $"/api/backoffice/projects/{firstProject}", new { name = "Forbidden" })).StatusCode);
        await SeedTickets(factory, firstProject, secondProject);

        var instructions = await PutJson(adminAgent, $"/api/backoffice/projects/{firstProject}/support-instructions", new
        {
            markdown = "# First product\n\nAsk for the release number.",
        });
        Assert.Equal(HttpStatusCode.OK, instructions.StatusCode);

        var list = await Read(await supportAgent.GetAsync("/api/backoffice/tickets", TestContext.Current.CancellationToken));
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
        var scopedOut = await Read(await supportAgent.GetAsync(
            $"/api/backoffice/tickets?project_id={secondProject}",
            TestContext.Current.CancellationToken));
        Assert.Empty(scopedOut.GetProperty("items").EnumerateArray());
        var searched = await Read(await supportAgent.GetAsync(
            "/api/backoffice/tickets?search=blank",
            TestContext.Current.CancellationToken));
        Assert.Single(searched.GetProperty("items").EnumerateArray());

        var firstPage = await Read(await supportAgent.GetAsync("/api/backoffice/tickets?limit=1", TestContext.Current.CancellationToken));
        var cursor = firstPage.GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor);
        var secondPage = await Read(await supportAgent.GetAsync(
            $"/api/backoffice/tickets?limit=1&cursor={Uri.EscapeDataString(cursor!)}",
            TestContext.Current.CancellationToken));
        Assert.Single(secondPage.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await supportAgent.GetAsync(
                $"/api/backoffice/tickets?limit=1&status=open&cursor={Uri.EscapeDataString(cursor!)}",
                TestContext.Current.CancellationToken)).StatusCode);

        var context = await Read(await supportAgent.GetAsync("/api/backoffice/tickets/HLP-201", TestContext.Current.CancellationToken));
        Assert.Equal("# First product\n\nAsk for the release number.", context.GetProperty("support_instructions").GetString());
        Assert.Single(context.GetProperty("conversation").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PostJson(supportAgent, "/api/backoffice/tickets/HLP-201/replies", new { message = "Must not persist", status = "open" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PatchJson(supportAgent, "/api/backoffice/tickets/HLP-201", new { priority = "urgent", unknown_field = true })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutJson(supportAgent, $"/api/backoffice/projects/{firstProject}/support-instructions", new { markdown = "Forbidden" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await supportAgent.GetAsync("/api/backoffice/tickets/HLP-202", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostJson(supportAgent, "/api/backoffice/tickets/HLP-202/notes", new { message = "Forbidden" })).StatusCode);

        var invalidAssignee = await PatchJson(supportAgent, "/api/backoffice/tickets/HLP-201", new { assignee_id = otherSupport });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAssignee.StatusCode);
        var assigned = await PatchJson(supportAgent, "/api/backoffice/tickets/HLP-201", new { assignee_id = support, priority = "urgent" });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var mine = await Read(await supportAgent.GetAsync("/api/backoffice/tickets?mine=true", TestContext.Current.CancellationToken));
        Assert.Single(mine.GetProperty("items").EnumerateArray());

        var note = await PostJson(supportAgent, "/api/backoffice/tickets/HLP-201/notes", new { message = "Reproduced in production." });
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        var reply = await PostJson(supportAgent, "/api/backoffice/tickets/HLP-201/replies", new
        {
            message = "Please try the new release.",
            status = "waiting_for_customer",
        });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var replyDocument = await Read(reply);
        Assert.Equal("waiting_for_customer", replyDocument.GetProperty("summary").GetProperty("status").GetString());
        Assert.Contains(replyDocument.GetProperty("conversation").EnumerateArray(),
            value => value.GetProperty("kind").GetString() == "public_reply" &&
                     value.GetProperty("actor").GetProperty("agent_name").GetString() == "Support agent");

        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await database.Tickets.Include(value => value.Conversation)
            .SingleAsync(value => value.Number == "HLP-201", TestContext.Current.CancellationToken);
        Assert.Equal(TicketStatus.WaitingForCustomer, stored.Status);
        Assert.Equal(TicketPriority.Urgent, stored.Priority);
        Assert.Equal(support, stored.AssigneeUserId);
        Assert.Contains(stored.Conversation, value => value.Kind is ConversationEntryKind.InternalNote && value.ActingAgentCredentialId is not null);
        Assert.Contains(stored.Conversation, value => value.Kind is ConversationEntryKind.PublicReply && value.ActingAgentCredentialId is not null);
        Assert.DoesNotContain(stored.Conversation, value => value.Body == "Must not persist");
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
        var response = await client.PostAsJsonAsync("/api/backoffice/session", new { email, password }, TestContext.Current.CancellationToken);
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
        var response = await PostJson(admin, "/api/backoffice/projects", new { key, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateUser(HttpClient admin, string name, string email, string password)
    {
        var response = await PostJson(admin, "/api/backoffice/users", new { name, email, password, role = "support" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read(response)).GetProperty("id").GetGuid();
    }

    private static async Task Grant(HttpClient admin, Guid userId, Guid projectId)
    {
        var response = await admin.PutAsync($"/api/backoffice/users/{userId}/projects/{projectId}", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpClient> CreateAgent(
        HttpClient admin,
        WebApplicationFactory<Program> factory,
        Guid? userId,
        string name)
    {
        var response = await PostJson(admin, "/api/backoffice/agents", new
        {
            name,
            user_id = userId,
            all_projects = true,
            project_ids = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = (await Read(response)).GetProperty("token").GetString()!;
        return BearerClient(factory, token);
    }

    private static async Task SeedTickets(WebApplicationFactory<Program> factory, Guid firstProject, Guid secondProject)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        database.Tickets.Add(Ticket.Create(Guid.NewGuid(), "HLP-201", firstProject, "Settings are blank", "first-user", "First User", "first@example.test", "The page is blank.", DateTimeOffset.UtcNow.AddMinutes(-3)));
        database.Tickets.Add(Ticket.Create(Guid.NewGuid(), "HLP-202", secondProject, "Second project ticket", "second-user", "Second User", "second@example.test", "Help needed.", DateTimeOffset.UtcNow.AddMinutes(-2)));
        database.Tickets.Add(Ticket.Create(Guid.NewGuid(), "HLP-203", firstProject, "Another request", "third-user", "Third User", "third@example.test", "Another question.", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PutJson(HttpClient client, string path, object body) =>
        client.PutAsJsonAsync(path, body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PatchJson(HttpClient client, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<JsonElement> Read(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
