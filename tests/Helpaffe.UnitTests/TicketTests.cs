using Helpaffe.Domain.Tickets;

namespace Helpaffe.UnitTests;

public sealed class TicketTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_ticket_has_fixed_identity_normal_priority_and_public_initial_message()
    {
        var projectId = Guid.NewGuid();

        var ticket = CreateTicket(projectId);

        Assert.Equal("HLP-42", ticket.Number);
        Assert.Equal(projectId, ticket.ProjectId);
        Assert.Equal("external-7", ticket.RequesterExternalId);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Equal(TicketPriority.Normal, ticket.Priority);
        Assert.Equal(1, ticket.Version);
        Assert.Equal(CreatedAt, ticket.WaitingSince);
        Assert.Null(ticket.AssigneeUserId);
        var initial = Assert.Single(ticket.Conversation);
        Assert.Equal(ConversationEntryKind.CustomerMessage, initial.Kind);
        Assert.True(initial.IsPublic);
        Assert.Null(initial.ActorUserId);
        Assert.Null(initial.ActingAgentCredentialId);
    }

    [Theory]
    [InlineData(TicketStatus.WaitingForCustomer, TicketStatus.Open)]
    [InlineData(TicketStatus.Resolved, TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Open, TicketStatus.Open)]
    public void Customer_reply_applies_the_fixed_reopening_rule(TicketStatus before, TicketStatus after)
    {
        var userId = Guid.NewGuid();
        var ticket = CreateTicket();
        ticket.ChangeStatus(before, userId, null, CreatedAt.AddMinutes(1));

        ticket.AddCustomerMessage(Guid.NewGuid(), "More information", CreatedAt.AddMinutes(2));

        Assert.Equal(after, ticket.Status);
        Assert.Equal(CreatedAt.AddMinutes(2), ticket.LastCustomerReplyAt);
        Assert.Equal(CreatedAt.AddMinutes(2), ticket.UpdatedAt);
    }

    [Fact]
    public void Conversation_distinguishes_visibility_and_records_human_and_agent()
    {
        var userId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var ticket = CreateTicket();

        ticket.AddInternalNote(Guid.NewGuid(), userId, agentId, "Only support sees this.", CreatedAt.AddMinutes(1));
        ticket.AddPublicReply(Guid.NewGuid(), userId, agentId, "The customer sees this.", TicketStatus.WaitingForCustomer, CreatedAt.AddMinutes(2));

        var note = Assert.Single(ticket.Conversation, value => value.Kind is ConversationEntryKind.InternalNote);
        Assert.False(note.IsPublic);
        Assert.Equal(userId, note.ActorUserId);
        Assert.Equal(agentId, note.ActingAgentCredentialId);
        var reply = Assert.Single(ticket.Conversation, value => value.Kind is ConversationEntryKind.PublicReply);
        Assert.True(reply.IsPublic);
        Assert.Equal(userId, reply.ActorUserId);
        Assert.Equal(agentId, reply.ActingAgentCredentialId);
        Assert.Contains(ticket.Conversation, value => value.Kind is ConversationEntryKind.SystemEvent && !value.IsPublic);
        Assert.Equal(TicketStatus.WaitingForCustomer, ticket.Status);
    }

    [Fact]
    public void Assignment_and_priority_changes_preserve_the_responsible_user_when_an_agent_acts()
    {
        var userId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var ticket = CreateTicket();

        ticket.Assign(userId, userId, agentId, CreatedAt.AddMinutes(1));
        ticket.ChangePriority(TicketPriority.Urgent, userId, agentId, CreatedAt.AddMinutes(2));

        Assert.Equal(userId, ticket.AssigneeUserId);
        Assert.Equal(TicketPriority.Urgent, ticket.Priority);
        Assert.All(
            ticket.Conversation.Where(value => value.Kind is ConversationEntryKind.SystemEvent),
            value =>
            {
                Assert.Equal(userId, value.ActorUserId);
                Assert.Equal(agentId, value.ActingAgentCredentialId);
        });
    }

    [Fact]
    public void Conversation_entries_do_not_expose_mutation()
    {
        var properties = typeof(ConversationEntry).GetProperties();

        Assert.All(properties.Where(value => value.SetMethod is not null), value => Assert.False(value.SetMethod!.IsPublic));
    }

    [Fact]
    public void Further_customer_messages_do_not_reset_an_already_open_tickets_wait()
    {
        var ticket = CreateTicket();

        ticket.AddCustomerMessage(Guid.NewGuid(), "One more detail", CreatedAt.AddMinutes(5));

        Assert.Equal(CreatedAt, ticket.WaitingSince);
        Assert.Equal(2, ticket.Version);
    }

    [Fact]
    public void Reopening_resets_waiting_since_and_one_atomic_update_advances_version_once()
    {
        var userId = Guid.NewGuid();
        var ticket = CreateTicket();
        ticket.ChangeStatus(TicketStatus.Resolved, userId, null, CreatedAt.AddMinutes(1));
        var beforeReply = ticket.Version;

        ticket.AddCustomerMessage(Guid.NewGuid(), "This is not fixed", CreatedAt.AddMinutes(10));
        var beforeCombinedUpdate = ticket.Version;
        ticket.Update(TicketStatus.InProgress, TicketPriority.Urgent, true, userId, userId, null, CreatedAt.AddMinutes(11));

        Assert.Equal(TicketStatus.InProgress, ticket.Status);
        Assert.Equal(TicketPriority.Urgent, ticket.Priority);
        Assert.Equal(userId, ticket.AssigneeUserId);
        Assert.Equal(CreatedAt.AddMinutes(10), ticket.WaitingSince);
        Assert.Equal(beforeReply + 1, beforeCombinedUpdate);
        Assert.Equal(beforeCombinedUpdate + 1, ticket.Version);
    }

    [Fact]
    public void Snooze_is_a_versioned_audited_field_and_must_end_in_the_future()
    {
        var userId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var ticket = CreateTicket();
        var until = CreatedAt.AddDays(2);

        ticket.SetSnooze(until, userId, agentId, CreatedAt.AddMinutes(1));

        Assert.Equal(until, ticket.SnoozedUntil);
        Assert.Equal(2, ticket.Version);
        Assert.Contains(ticket.Conversation, entry =>
            entry.Kind is ConversationEntryKind.SystemEvent &&
            entry.Body.Contains("not set", StringComparison.Ordinal) &&
            entry.Body.Contains(until.ToString("O"), StringComparison.Ordinal) &&
            entry.ActorUserId == userId &&
            entry.ActingAgentCredentialId == agentId);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ticket.SetSnooze(CreatedAt, userId, agentId, CreatedAt.AddMinutes(2)));

        ticket.SetSnooze(null, userId, agentId, CreatedAt.AddMinutes(3));

        Assert.Null(ticket.SnoozedUntil);
        Assert.Equal(3, ticket.Version);
        Assert.Contains(ticket.Conversation, entry =>
            entry.Kind is ConversationEntryKind.SystemEvent &&
            entry.Body.Contains(until.ToString("O"), StringComparison.Ordinal) &&
            entry.Body.EndsWith("not set.", StringComparison.Ordinal));
    }

    [Fact]
    public void Customer_reply_wakes_a_snoozed_ticket_without_an_extra_version_increment()
    {
        var userId = Guid.NewGuid();
        var ticket = CreateTicket();
        ticket.SetSnooze(CreatedAt.AddDays(2), userId, null, CreatedAt.AddMinutes(1));
        var beforeReply = ticket.Version;

        ticket.AddCustomerMessage(Guid.NewGuid(), "Please look again", CreatedAt.AddMinutes(2));

        Assert.Null(ticket.SnoozedUntil);
        Assert.Equal(beforeReply + 1, ticket.Version);
        Assert.Contains(ticket.Conversation, entry =>
            entry.Kind is ConversationEntryKind.SystemEvent &&
            entry.Body.StartsWith("Snooze cleared by customer reply", StringComparison.Ordinal));
    }

    private static Ticket CreateTicket(Guid? projectId = null) => Ticket.Create(
        Guid.NewGuid(),
        "HLP-42",
        projectId ?? Guid.NewGuid(),
        "Cannot open settings",
        "external-7",
        "Ada User",
        "ada@example.test",
        "The settings page is blank.",
        CreatedAt,
        Guid.NewGuid());
}
