using System.Text;
using System.Text.Json;

namespace Helpaffe.Domain.Tickets;

public sealed class Ticket
{
    private readonly List<ConversationEntry> _conversation = [];
    private readonly List<DevelopmentReference> _developmentReferences = [];
    private readonly List<TicketAttachment> _attachments = [];

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
        Guid? initialEntryId = null,
        string? contextJson = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("A ticket id is required.", nameof(id));
        if (projectId == Guid.Empty) throw new ArgumentException("A project id is required.", nameof(projectId));
        Required(number, nameof(number), 32);
        Required(subject, nameof(subject), 300);
        Required(requesterExternalId, nameof(requesterExternalId), 200);
        Required(requesterName, nameof(requesterName), 200);
        Required(requesterEmail, nameof(requesterEmail), 320);
        ValidateContext(contextJson);

        var ticket = new Ticket
        {
            Id = id,
            Number = number.Trim().ToUpperInvariant(),
            ProjectId = projectId,
            Subject = subject.Trim(),
            RequesterExternalId = requesterExternalId.Trim(),
            RequesterName = requesterName.Trim(),
            RequesterEmail = requesterEmail.Trim(),
            ContextJson = contextJson,
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
    public string? ContextJson { get; private set; }
    public Guid? AssigneeUserId { get; private set; }
    public TicketPriority Priority { get; private set; }
    public TicketStatus Status { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset LastCustomerReplyAt { get; private set; }
    public DateTimeOffset WaitingSince { get; private set; }
    public DateTimeOffset? SnoozedUntil { get; private set; }
    public IReadOnlyCollection<ConversationEntry> Conversation => _conversation.AsReadOnly();
    public IReadOnlyCollection<DevelopmentReference> DevelopmentReferences => _developmentReferences.AsReadOnly();
    public IReadOnlyCollection<TicketAttachment> Attachments => _attachments.AsReadOnly();

    public TicketAttachment AddAttachment(
        Guid attachmentId,
        Guid conversationEntryId,
        string fileName,
        string mediaType,
        long size,
        string storageKey,
        DateTimeOffset createdAt)
    {
        var entry = _conversation.SingleOrDefault(value => value.Id == conversationEntryId)
            ?? throw new ArgumentException("The conversation entry does not belong to this ticket.", nameof(conversationEntryId));
        if (entry.Kind is ConversationEntryKind.SystemEvent)
            throw new ArgumentException("System events cannot have attachments.", nameof(conversationEntryId));
        if (_attachments.Count(value => value.ConversationEntryId == conversationEntryId) >= TicketAttachment.MaximumFilesPerMessage)
            throw new InvalidOperationException($"A conversation entry may have at most {TicketAttachment.MaximumFilesPerMessage} attachments.");
        if (_attachments.Where(value => value.ConversationEntryId == conversationEntryId).Sum(value => value.Size) + size > TicketAttachment.MaximumTotalSize)
            throw new InvalidOperationException($"Attachments on one conversation entry may contain at most {TicketAttachment.MaximumTotalSize} bytes in total.");

        var attachment = new TicketAttachment(
            attachmentId,
            ProjectId,
            Id,
            conversationEntryId,
            fileName,
            mediaType,
            size,
            storageKey,
            entry.IsPublic,
            createdAt);
        _attachments.Add(attachment);
        return attachment;
    }

    public void AddCustomerMessage(Guid entryId, string body, DateTimeOffset createdAt)
    {
        AddEntry(entryId, ConversationEntryKind.CustomerMessage, body, createdAt);
        LastCustomerReplyAt = createdAt;
        if (SnoozedUntil is not null)
        {
            var previous = SnoozedUntil.Value;
            SnoozedUntil = null;
            AddEntry(Guid.NewGuid(), ConversationEntryKind.SystemEvent,
                $"Snooze cleared by customer reply (was {previous:O}).", createdAt);
        }
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

    public void SetSnooze(
        DateTimeOffset? snoozedUntil,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        var normalized = snoozedUntil?.ToUniversalTime();
        if (normalized is not null && normalized <= changedAt.ToUniversalTime())
            throw new ArgumentOutOfRangeException(nameof(snoozedUntil), "A snooze must end in the future.");
        if (SnoozedUntil == normalized) return;

        var previous = SnoozedUntil is null ? "not set" : SnoozedUntil.Value.ToString("O");
        var next = normalized is null ? "not set" : normalized.Value.ToString("O");
        SnoozedUntil = normalized;
        AddEntry(Guid.NewGuid(), ConversationEntryKind.SystemEvent,
            $"Snooze changed from {previous} to {next}.", changedAt, actorUserId, actingAgentCredentialId);
        UpdatedAt = changedAt;
        Version++;
    }

    public DevelopmentReference AddDevelopmentReference(
        Guid referenceId,
        DevelopmentReferenceType type,
        string url,
        string label,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        if (_developmentReferences.Any(value => string.Equals(value.Url, url.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The development reference already exists on this ticket.");
        var reference = new DevelopmentReference(
            referenceId,
            Id,
            _developmentReferences.Select(value => value.Position).DefaultIfEmpty().Max() + 1,
            type,
            url,
            label,
            changedAt);
        _developmentReferences.Add(reference);
        AddEntry(Guid.NewGuid(), ConversationEntryKind.SystemEvent,
            $"Development reference added: {type} {reference.Label} ({reference.Url}).",
            changedAt, actorUserId, actingAgentCredentialId);
        UpdatedAt = changedAt;
        Version++;
        return reference;
    }

    public DevelopmentReference? RemoveDevelopmentReference(
        Guid referenceId,
        Guid actorUserId,
        Guid? actingAgentCredentialId,
        DateTimeOffset changedAt)
    {
        var reference = _developmentReferences.SingleOrDefault(value => value.Id == referenceId);
        if (reference is null) return null;
        _developmentReferences.Remove(reference);
        AddEntry(Guid.NewGuid(), ConversationEntryKind.SystemEvent,
            $"Development reference removed: {reference.Type} {reference.Label} ({reference.Url}).",
            changedAt, actorUserId, actingAgentCredentialId);
        UpdatedAt = changedAt;
        Version++;
        return reference;
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

    private static void ValidateContext(string? contextJson)
    {
        if (contextJson is null) return;
        if (Encoding.UTF8.GetByteCount(contextJson) > 16 * 1024)
            throw new ArgumentOutOfRangeException(nameof(contextJson), "Ticket context may contain at most 16 KB of UTF-8 JSON.");
        using var document = JsonDocument.Parse(contextJson);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
            throw new ArgumentException("Ticket context must be a JSON object.", nameof(contextJson));
    }
}
