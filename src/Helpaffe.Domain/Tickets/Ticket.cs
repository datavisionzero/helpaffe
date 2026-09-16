namespace Helpaffe.Domain.Tickets;

public sealed class Ticket
{
    private readonly List<ConversationEntry> _conversation = [];

    private Ticket()
    {
    }

    public static Ticket Create(
        Guid id,
        string number,
        Guid projectId,
        string subject,
        string requesterExternalId,
        string requesterName,
        string requesterEmail,
        string initialMessage,
        DateTimeOffset createdAt,
        Guid? initialEntryId = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("A ticket id is required.", nameof(id));
        if (projectId == Guid.Empty) throw new ArgumentException("A project id is required.", nameof(projectId));
        Required(number, nameof(number), 32);
        Required(subject, nameof(subject), 300);
        Required(requesterExternalId, nameof(requesterExternalId), 200);
        Required(requesterName, nameof(requesterName), 200);
        Required(requesterEmail, nameof(requesterEmail), 320);

        var ticket = new Ticket
        {
            Id = id,
            Number = number.Trim().ToUpperInvariant(),
            ProjectId = projectId,
            Subject = subject.Trim(),
            RequesterExternalId = requesterExternalId.Trim(),
            RequesterName = requesterName.Trim(),
            RequesterEmail = requesterEmail.Trim(),
            Status = TicketStatus.Open,
            Priority = TicketPriority.Normal,
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastCustomerReplyAt = createdAt,
            WaitingSince = createdAt,
        };
        ticket._conversation.Add(new ConversationEntry(
            initialEntryId ?? Guid.NewGuid(), ticket.Id, 1, ConversationEntryKind.CustomerMessage, initialMessage, createdAt));
        return ticket;
    }

    public Guid Id { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public Guid ProjectId { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string RequesterExternalId { get; private set; } = string.Empty;
    public string RequesterName { get; private set; } = string.Empty;
    public string RequesterEmail { get; private set; } = string.Empty;
    public Guid? AssigneeUserId { get; private set; }
    public TicketPriority Priority { get; private set; }
    public TicketStatus Status { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset LastCustomerReplyAt { get; private set; }
    public DateTimeOffset WaitingSince { get; private set; }
    public IReadOnlyCollection<ConversationEntry> Conversation => _conversation.AsReadOnly();

    public void AddCustomerMessage(Guid entryId, string body, DateTimeOffset createdAt)
    {
        AddEntry(entryId, ConversationEntryKind.CustomerMessage, body, createdAt);
        LastCustomerReplyAt = createdAt;
        if (Status is TicketStatus.WaitingForCustomer or TicketStatus.Resolved)
            ChangeStatusCore(TicketStatus.Open, createdAt, null, null, "Customer reply reopened the ticket.");
        UpdatedAt = createdAt;
        Version++;
    }

    public void AddPublicReply(
        Guid entryId,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        string body,
        TicketStatus resultingStatus,
        DateTimeOffset createdAt)
    {
        if (resultingStatus is TicketStatus.Open)
            throw new ArgumentException("A public reply must continue work, wait for the customer, or resolve the ticket.", nameof(resultingStatus));
        AddEntry(entryId, ConversationEntryKind.PublicReply, body, createdAt, actorUserId, actingAgentCredentialId);
        ChangeStatusCore(resultingStatus, createdAt, actorUserId, actingAgentCredentialId, $"Status changed to {resultingStatus}.");
        UpdatedAt = createdAt;
        Version++;
    }

    public void AddInternalNote(
        Guid entryId,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        string body,
        DateTimeOffset createdAt)
    {
        AddEntry(entryId, ConversationEntryKind.InternalNote, body, createdAt, actorUserId, actingAgentCredentialId);
        UpdatedAt = createdAt;
        Version++;
    }

    public void ChangeStatus(
        TicketStatus status,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        if (ChangeStatusCore(status, changedAt, actorUserId, actingAgentCredentialId, $"Status changed to {status}."))
        {
            UpdatedAt = changedAt;
            Version++;
        }
    }

    public void ChangePriority(
        TicketPriority priority,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        if (ChangePriorityCore(priority, actorUserId, actingAgentCredentialId, changedAt))
        {
            UpdatedAt = changedAt;
            Version++;
        }
    }

    public void Assign(
        Guid? assigneeUserId,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        if (assigneeUserId == Guid.Empty) throw new ArgumentException("An assignee id cannot be empty.", nameof(assigneeUserId));
        if (AssignCore(assigneeUserId, actorUserId, actingAgentCredentialId, changedAt))
        {
            UpdatedAt = changedAt;
            Version++;
        }
    }

    public void Update(
        TicketStatus? status,
        TicketPriority? priority,
        bool changeAssignee,
        Guid? assigneeUserId,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        if (changeAssignee && assigneeUserId == Guid.Empty)
            throw new ArgumentException("An assignee id cannot be empty.", nameof(assigneeUserId));
        var changed = false;
        if (status is not null)
            changed |= ChangeStatusCore(status.Value, changedAt, actorUserId, actingAgentCredentialId, $"Status changed to {status.Value}.");
        if (priority is not null)
            changed |= ChangePriorityCore(priority.Value, actorUserId, actingAgentCredentialId, changedAt);
        if (changeAssignee)
            changed |= AssignCore(assigneeUserId, actorUserId, actingAgentCredentialId, changedAt);
        if (changed)
        {
            UpdatedAt = changedAt;
            Version++;
        }
    }

    private bool ChangeStatusCore(
        TicketStatus status,
        DateTimeOffset changedAt,
        Guid? actorUserId,
        Guid? actingAgentCredentialId,
        string description)
    {
        if (Status == status) return false;
        Status = status;
        if (status is TicketStatus.Open) WaitingSince = changedAt;
        AddEntry(Guid.NewGuid(), ConversationEntryKind.SystemEvent, description, changedAt, actorUserId, actingAgentCredentialId);
        return true;
    }

    private bool ChangePriorityCore(
        TicketPriority priority,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        if (Priority == priority) return false;
        Priority = priority;
        AddEntry(Guid.NewGuid(), ConversationEntryKind.SystemEvent, $"Priority changed to {priority}.", changedAt, actorUserId, actingAgentCredentialId);
        return true;
    }

    private bool AssignCore(
        Guid? assigneeUserId,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        if (AssigneeUserId == assigneeUserId) return false;
        AssigneeUserId = assigneeUserId;
        var description = assigneeUserId is null ? "Ticket unassigned." : "Ticket assigned to a support user.";
        AddEntry(Guid.NewGuid(), ConversationEntryKind.SystemEvent, description, changedAt, actorUserId, actingAgentCredentialId);
        return true;
    }

    private void AddEntry(
        Guid id,
        ConversationEntryKind kind,
        string body,
        DateTimeOffset createdAt,
        Guid? actorUserId = null,
        Guid? actingAgentCredentialId = null) =>
        _conversation.Add(new ConversationEntry(
            id,
            Id,
            _conversation.Count + 1,
            kind,
            body,
            createdAt,
            actorUserId,
            actingAgentCredentialId));

    private static void Required(string value, string parameter, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", parameter);
        if (value.Trim().Length > maximumLength) throw new ArgumentOutOfRangeException(parameter);
    }
}
