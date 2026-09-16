using System.Text.Json;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;

namespace Helpaffe.Infrastructure.Notifications;

public sealed record NotificationData(
    string ProductName,
    string CustomerName,
    string CustomerEmail,
    string TicketNumber,
    string TicketSubject,
    string Message,
    string AssigneeName);

public static class NotificationOutbox
{
    public const string Pending = "pending";
    public const string Submitted = "submitted_to_smtp";
    public const string Failed = "failed";
    public const string SupportRecipients = "support_recipients";
    public const string Customer = "customer";
    public const string Assignee = "assignee";

    public static void AddNewTicket(HelpaffeDbContext database, Ticket ticket, ProjectRecord project, DateTimeOffset now)
    {
        var data = Data(ticket, project, ticket.Conversation.OrderBy(value => value.Sequence).First().Body, string.Empty);
        Add(database, ticket, "new_ticket_customer", Customer, ticket.RequesterEmail, ticket.RequesterName, data, now);
        Add(database, ticket, "new_ticket_support", SupportRecipients, null, null, data, now);
    }

    public static void AddCustomerReply(
        HelpaffeDbContext database,
        Ticket ticket,
        ProjectRecord project,
        string message,
        UserRecord? assignee,
        DateTimeOffset now)
    {
        var data = Data(ticket, project, message, assignee?.Name ?? string.Empty);
        Add(
            database,
            ticket,
            "customer_reply_support",
            assignee is null ? SupportRecipients : Assignee,
            assignee?.Email,
            assignee?.Name,
            data,
            now);
    }

    public static void AddSupportReply(
        HelpaffeDbContext database,
        Ticket ticket,
        ProjectRecord project,
        string message,
        DateTimeOffset now) =>
        Add(database, ticket, "public_reply_customer", Customer, ticket.RequesterEmail, ticket.RequesterName,
            Data(ticket, project, message, string.Empty), now);

    public static void AddAssignment(
        HelpaffeDbContext database,
        Ticket ticket,
        ProjectRecord project,
        UserRecord assignee,
        DateTimeOffset now) =>
        Add(database, ticket, "assignment_support", Assignee, assignee.Email, assignee.Name,
            Data(ticket, project, string.Empty, assignee.Name), now);

    private static NotificationData Data(
        Ticket ticket,
        ProjectRecord project,
        string message,
        string assigneeName) => new(
        project.Name,
        ticket.RequesterName,
        ticket.RequesterEmail,
        ticket.Number,
        ticket.Subject,
        message,
        assigneeName);

    private static void Add(
        HelpaffeDbContext database,
        Ticket ticket,
        string type,
        string targetKind,
        string? recipientEmail,
        string? recipientName,
        NotificationData data,
        DateTimeOffset now) =>
        database.NotificationDeliveries.Add(new NotificationDeliveryRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = ticket.ProjectId,
            TicketId = ticket.Id,
            Type = type,
            TargetKind = targetKind,
            RecipientEmail = recipientEmail,
            RecipientName = recipientName,
            DataJson = JsonSerializer.Serialize(data),
            Status = Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
        });
}
