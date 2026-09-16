using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class TicketPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-tests")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Ticket_project_requester_workflow_and_actor_history_round_trip()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var ticketId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        Guid adminId;

        await using (var scope = factory.Services.CreateAsyncScope())
        await using (var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            var admin = await database.Users.SingleAsync(TestContext.Current.CancellationToken);
            adminId = admin.Id;
            database.Projects.Add(new ProjectRecord { Id = projectId, Key = "PRODUCT", Name = "Product" });
            database.AgentCredentials.Add(new AgentCredentialRecord
            {
                Id = agentId,
                UserId = admin.Id,
                Name = "Support agent",
                TokenHash = AccessToken.Hash("hfa_persistence-test"),
                TokenPrefix = "hfa_persist",
                AllProjects = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);

            var createdAt = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var ticket = Ticket.Create(
                ticketId,
                "HLP-100",
                projectId,
                "Settings are blank",
                "external-user-7",
                "Ada User",
                "ada@example.test",
                "I cannot open settings.",
                createdAt);
            ticket.Assign(admin.Id, admin.Id, agentId, createdAt.AddMinutes(1));
            ticket.ChangePriority(TicketPriority.Urgent, admin.Id, agentId, createdAt.AddMinutes(2));
            ticket.AddInternalNote(Guid.NewGuid(), admin.Id, agentId, "Reproduced on production.", createdAt.AddMinutes(3));
            ticket.AddPublicReply(Guid.NewGuid(), admin.Id, agentId, "Please try again.", TicketStatus.WaitingForCustomer, createdAt.AddMinutes(4));
            ticket.AddCustomerMessage(Guid.NewGuid(), "It still fails.", createdAt.AddMinutes(5));
            database.Tickets.Add(ticket);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        await using (var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            var ticket = await database.Tickets.AsNoTracking()
                .Include(value => value.Conversation)
                .SingleAsync(value => value.Id == ticketId, TestContext.Current.CancellationToken);
            Assert.Equal("HLP-100", ticket.Number);
            Assert.Equal(projectId, ticket.ProjectId);
            Assert.Equal("external-user-7", ticket.RequesterExternalId);
            Assert.Equal(adminId, ticket.AssigneeUserId);
            Assert.Equal(TicketPriority.Urgent, ticket.Priority);
            Assert.Equal(TicketStatus.Open, ticket.Status);
            Assert.Equal(6, ticket.Version);
            Assert.Equal(new DateTimeOffset(2026, 9, 16, 12, 5, 0, TimeSpan.Zero), ticket.WaitingSince);
            Assert.Equal(8, ticket.Conversation.Count);
            Assert.Equal(Enumerable.Range(1, 8), ticket.Conversation.OrderBy(value => value.Sequence).Select(value => value.Sequence));
            var note = Assert.Single(ticket.Conversation, value => value.Kind is ConversationEntryKind.InternalNote);
            Assert.Equal(adminId, note.ActorUserId);
            Assert.Equal(agentId, note.ActingAgentCredentialId);
            Assert.False(note.IsPublic);
        }
    }

    [Fact]
    public async Task Ticket_numbers_are_unique_across_projects()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        await using var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        var firstProject = new ProjectRecord { Id = Guid.NewGuid(), Key = "FIRST", Name = "First" };
        var secondProject = new ProjectRecord { Id = Guid.NewGuid(), Key = "SECOND", Name = "Second" };
        database.Projects.AddRange(firstProject, secondProject);
        database.Tickets.Add(CreateTicket("HLP-101", firstProject.Id));
        database.Tickets.Add(CreateTicket("HLP-101", secondProject.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _postgres.GetConnectionString());
            builder.UseSetting("Bootstrap:Name", "Admin");
            builder.UseSetting("Bootstrap:Email", "admin@example.test");
            builder.UseSetting("Bootstrap:Password", "admin-password-for-tests");
        });

    private static Ticket CreateTicket(string number, Guid projectId) => Ticket.Create(
        Guid.NewGuid(),
        number,
        projectId,
        "Subject",
        Guid.NewGuid().ToString("N"),
        "Requester",
        "requester@example.test",
        "Initial message",
        DateTimeOffset.UtcNow);
}
