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
        var assignees = await Read(await supportAgent.GetAsync(
            $"/api/backoffice/assignees?project_id={firstProject}",
            TestContext.Current.CancellationToken));
        Assert.Contains(assignees.EnumerateArray(), value => value.GetProperty("id").GetGuid() == support);
        Assert.DoesNotContain(assignees.EnumerateArray(), value => value.GetProperty("id").GetGuid() == otherSupport);
        Assert.Equal(HttpStatusCode.NotFound,
            (await supportAgent.GetAsync($"/api/backoffice/assignees?project_id={secondProject}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await PatchJson(adminAgent, $"/api/backoffice/projects/{firstProject}", new { name = "First product renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PatchJson(supportAgent, $"/api/backoffice/projects/{firstProject}", new { name = "Forbidden" })).StatusCode);
        await SeedTickets(factory, firstProject, secondProject, support);

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
        var resolvedSearch = await Read(await supportAgent.GetAsync(
            $"/api/backoffice/tickets?search=cache&status=resolved&project_id={firstProject}",
            TestContext.Current.CancellationToken));
        Assert.Equal("HLP-203", resolvedSearch.GetProperty("items")[0].GetProperty("number").GetString());
        var hiddenSearch = await Read(await supportAgent.GetAsync(
            "/api/backoffice/tickets?search=second",
            TestContext.Current.CancellationToken));
        Assert.Empty(hiddenSearch.GetProperty("items").EnumerateArray());
        var requesterTickets = await Read(await adminAgent.GetAsync(
            "/api/backoffice/tickets/HLP-201/requester-tickets",
            TestContext.Current.CancellationToken));
        Assert.Single(requesterTickets.GetProperty("items").EnumerateArray());
        Assert.Equal("HLP-203", requesterTickets.GetProperty("items")[0].GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await supportAgent.GetAsync("/api/backoffice/tickets/HLP-202/requester-tickets", TestContext.Current.CancellationToken)).StatusCode);

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

        var contextResponse = await supportAgent.GetAsync("/api/backoffice/tickets/HLP-201", TestContext.Current.CancellationToken);
        Assert.Equal("\"1\"", contextResponse.Headers.ETag?.Tag);
        var context = await Read(contextResponse);
        var version = context.GetProperty("summary").GetProperty("version").GetInt32();
        Assert.Equal("# First product\n\nAsk for the release number.", context.GetProperty("support_instructions").GetString());
        Assert.Single(context.GetProperty("conversation").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendTicketJson(supportAgent, HttpMethod.Post, "/api/backoffice/tickets/HLP-201/replies", new { message = "Must not persist", status = "open" }, version, "invalid-reply")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendTicketJson(supportAgent, HttpMethod.Patch, "/api/backoffice/tickets/HLP-201", new { priority = "urgent", unknown_field = true }, version)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutJson(supportAgent, $"/api/backoffice/projects/{firstProject}/support-instructions", new { markdown = "Forbidden" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await supportAgent.GetAsync("/api/backoffice/tickets/HLP-202", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostJson(supportAgent, "/api/backoffice/tickets/HLP-202/notes", new { message = "Forbidden" })).StatusCode);

        var invalidAssignee = await SendTicketJson(supportAgent, HttpMethod.Patch, "/api/backoffice/tickets/HLP-201", new { assignee_id = otherSupport }, version);
        Assert.Equal(HttpStatusCode.BadRequest, invalidAssignee.StatusCode);
        var assigned = await SendTicketJson(supportAgent, HttpMethod.Patch, "/api/backoffice/tickets/HLP-201", new { assignee_id = support, priority = "urgent" }, version);
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        version = (await Read(assigned)).GetProperty("summary").GetProperty("version").GetInt32();
        var mine = await Read(await supportAgent.GetAsync("/api/backoffice/tickets?mine=true", TestContext.Current.CancellationToken));
        Assert.Single(mine.GetProperty("items").EnumerateArray());

        var snoozedUntil = DateTimeOffset.UtcNow.AddHours(2);
        var snoozed = await SendTicketJson(supportAgent, HttpMethod.Put, "/api/backoffice/tickets/HLP-201/snooze",
            new { snoozed_until = snoozedUntil }, version, "snooze-201");
        Assert.Equal(HttpStatusCode.OK, snoozed.StatusCode);
        var snoozedDocument = await Read(snoozed);
        version = snoozedDocument.GetProperty("summary").GetProperty("version").GetInt32();
        Assert.Equal(snoozedUntil, snoozedDocument.GetProperty("summary").GetProperty("snoozed_until").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        var replayedSnooze = await SendTicketJson(supportAgent, HttpMethod.Put, "/api/backoffice/tickets/HLP-201/snooze",
            new { snoozed_until = snoozedUntil }, version - 1, "snooze-201");
        Assert.Equal(version, (await Read(replayedSnooze)).GetProperty("summary").GetProperty("version").GetInt32());
        Assert.Empty((await Read(await supportAgent.GetAsync("/api/backoffice/tickets?mine=true", TestContext.Current.CancellationToken)))
            .GetProperty("items").EnumerateArray());
        Assert.Empty((await Read(await supportAgent.GetAsync("/api/backoffice/tickets?search=blank", TestContext.Current.CancellationToken)))
            .GetProperty("items").EnumerateArray());
        var directSnoozed = await Read(await supportAgent.GetAsync("/api/backoffice/tickets/HLP-201", TestContext.Current.CancellationToken));
        Assert.Equal(version, directSnoozed.GetProperty("summary").GetProperty("version").GetInt32());
        var noNext = await SendTicketJson(supportAgent, HttpMethod.Post, "/api/backoffice/tickets/next",
            new { project_id = firstProject }, 0, "next-while-snoozed");
        Assert.Equal(HttpStatusCode.NotFound, noNext.StatusCode);

        var unsnoozed = await SendTicketJson(supportAgent, HttpMethod.Put, "/api/backoffice/tickets/HLP-201/snooze",
            new { snoozed_until = (DateTimeOffset?)null }, version, "unsnooze-201");
        Assert.Equal(HttpStatusCode.OK, unsnoozed.StatusCode);
        var unsnoozedDocument = await Read(unsnoozed);
        version = unsnoozedDocument.GetProperty("summary").GetProperty("version").GetInt32();
        Assert.Equal(JsonValueKind.Null, unsnoozedDocument.GetProperty("summary").GetProperty("snoozed_until").ValueKind);
        Assert.Single((await Read(await supportAgent.GetAsync("/api/backoffice/tickets?mine=true", TestContext.Current.CancellationToken)))
            .GetProperty("items").EnumerateArray());

        var invalidReference = await SendTicketJson(supportAgent, HttpMethod.Post,
            "/api/backoffice/tickets/HLP-201/development-references",
            new { type = "github", url = "http://github.com/example/app/issues/42", label = "GH-42" }, version, "invalid-reference-201");
        Assert.Equal(HttpStatusCode.BadRequest, invalidReference.StatusCode);
        var addedReference = await SendTicketJson(supportAgent, HttpMethod.Post,
            "/api/backoffice/tickets/HLP-201/development-references",
            new { type = "github", url = "https://github.com/example/app/issues/42", label = "GH-42" }, version, "add-reference-201");
        Assert.Equal(HttpStatusCode.OK, addedReference.StatusCode);
        var addedReferenceDocument = await Read(addedReference);
        var reference = Assert.Single(addedReferenceDocument.GetProperty("development_references").EnumerateArray());
        var referenceId = reference.GetProperty("id").GetGuid();
        Assert.Equal(1, reference.GetProperty("position").GetInt32());
        Assert.Equal("github", reference.GetProperty("type").GetString());
        var versionBeforeReference = version;
        version = addedReferenceDocument.GetProperty("summary").GetProperty("version").GetInt32();
        var replayedReference = await SendTicketJson(supportAgent, HttpMethod.Post,
            "/api/backoffice/tickets/HLP-201/development-references",
            new { type = "github", url = "https://github.com/example/app/issues/42", label = "GH-42" }, versionBeforeReference, "add-reference-201");
        Assert.Single((await Read(replayedReference)).GetProperty("development_references").EnumerateArray());
        var duplicateReference = await SendTicketJson(supportAgent, HttpMethod.Post,
            "/api/backoffice/tickets/HLP-201/development-references",
            new { type = "gitlab", url = "https://github.com/example/app/issues/42", label = "duplicate" }, version, "duplicate-reference-201");
        Assert.Equal(HttpStatusCode.Conflict, duplicateReference.StatusCode);
        var removedReference = await SendTicketJson(supportAgent, HttpMethod.Delete,
            $"/api/backoffice/tickets/HLP-201/development-references/{referenceId}", new { }, version, "remove-reference-201");
        Assert.Equal(HttpStatusCode.OK, removedReference.StatusCode);
        var removedReferenceDocument = await Read(removedReference);
        Assert.Empty(removedReferenceDocument.GetProperty("development_references").EnumerateArray());
        version = removedReferenceDocument.GetProperty("summary").GetProperty("version").GetInt32();

        var note = await SendTicketJson(supportAgent, HttpMethod.Post, "/api/backoffice/tickets/HLP-201/notes", new { message = "Reproduced in production." }, version, "note-201");
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        version = (await Read(note)).GetProperty("summary").GetProperty("version").GetInt32();
        var replyBody = new
        {
            message = "Please try the new release.",
            status = "waiting_for_customer",
        };
        var reply = await SendTicketJson(supportAgent, HttpMethod.Post, "/api/backoffice/tickets/HLP-201/replies", replyBody, version, "reply-201");
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var replyDocument = await Read(reply);
        Assert.Equal("waiting_for_customer", replyDocument.GetProperty("summary").GetProperty("status").GetString());
        var repliedVersion = replyDocument.GetProperty("summary").GetProperty("version").GetInt32();
        Assert.Contains(replyDocument.GetProperty("conversation").EnumerateArray(),
            value => value.GetProperty("kind").GetString() == "public_reply" &&
                     value.GetProperty("actor").GetProperty("agent_name").GetString() == "Support agent");
        var replayed = await SendTicketJson(supportAgent, HttpMethod.Post, "/api/backoffice/tickets/HLP-201/replies", replyBody, version, "reply-201");
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(repliedVersion, (await Read(replayed)).GetProperty("summary").GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendTicketJson(supportAgent, HttpMethod.Post, "/api/backoffice/tickets/HLP-201/replies", new { message = "Different", status = "resolved" }, version, "reply-201")).StatusCode);
        var stale = await SendTicketJson(supportAgent, HttpMethod.Patch, "/api/backoffice/tickets/HLP-201", new { status = "resolved" }, version);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(repliedVersion, (await Read(stale)).GetProperty("current_version").GetInt32());
        Assert.Equal((HttpStatusCode)428,
            (await PatchJson(supportAgent, "/api/backoffice/tickets/HLP-201", new { status = "resolved" })).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await database.Tickets.Include(value => value.Conversation).Include(value => value.DevelopmentReferences)
            .SingleAsync(value => value.Number == "HLP-201", TestContext.Current.CancellationToken);
        Assert.Equal(TicketStatus.WaitingForCustomer, stored.Status);
        Assert.Equal(TicketPriority.Urgent, stored.Priority);
        Assert.Equal(support, stored.AssigneeUserId);
        Assert.Contains(stored.Conversation, value => value.Kind is ConversationEntryKind.InternalNote && value.ActingAgentCredentialId is not null);
        Assert.Contains(stored.Conversation, value => value.Kind is ConversationEntryKind.PublicReply && value.ActingAgentCredentialId is not null);
        Assert.Single(stored.Conversation, value => value.Kind is ConversationEntryKind.PublicReply);
        Assert.Equal(2, stored.Conversation.Count(value => value.Kind is ConversationEntryKind.SystemEvent && value.Body.StartsWith("Snooze changed", StringComparison.Ordinal)));
        Assert.Single(stored.Conversation, value => value.Body.StartsWith("Development reference added", StringComparison.Ordinal));
        Assert.Single(stored.Conversation, value => value.Body.StartsWith("Development reference removed", StringComparison.Ordinal));
        Assert.Empty(stored.DevelopmentReferences);
        Assert.DoesNotContain(stored.Conversation, value => value.Body == "Must not persist");
        Assert.Equal(repliedVersion, stored.Version);
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

    private static async Task SeedTickets(
        WebApplicationFactory<Program> factory,
        Guid firstProject,
        Guid secondProject,
        Guid supportUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        const string sharedRequesterId = "shared-user";
        database.Tickets.Add(Ticket.Create(Guid.NewGuid(), "HLP-201", firstProject, "Settings are blank", sharedRequesterId, "First User", "first@example.test", "The page is blank.", DateTimeOffset.UtcNow.AddMinutes(-3)));
        database.Tickets.Add(Ticket.Create(Guid.NewGuid(), "HLP-202", secondProject, "Second project ticket", sharedRequesterId, "First User", "first@example.test", "Help needed in the second project.", DateTimeOffset.UtcNow.AddMinutes(-2)));
        var previous = Ticket.Create(Guid.NewGuid(), "HLP-203", firstProject, "Another request", sharedRequesterId, "First User", "first@example.test", "A recurring cache invalidation problem.", DateTimeOffset.UtcNow.AddMinutes(-1));
        previous.ChangeStatus(TicketStatus.Resolved, supportUserId, null, DateTimeOffset.UtcNow);
        database.Tickets.Add(previous);
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

    private static Task<HttpResponseMessage> SendTicketJson(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        int version,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<JsonElement> Read(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
