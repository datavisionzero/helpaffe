namespace Helpaffe.Domain.Tickets;

public enum ConversationEntryKind
{
    CustomerMessage,
    PublicReply,
    InternalNote,
    SystemEvent,
}

public sealed class ConversationEntry
{
    private ConversationEntry()
    {
    }

    internal ConversationEntry(
        Guid id,
        Guid ticketId,
        int sequence,
        ConversationEntryKind kind,
        string body,
        DateTimeOffset createdAt,
        Guid? actorUserId = null,
        Guid? actingAgentCredentialId = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("An entry id is required.", nameof(id));
        if (ticketId == Guid.Empty) throw new ArgumentException("A ticket id is required.", nameof(ticketId));
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("Conversation content is required.", nameof(body));
        if (kind is ConversationEntryKind.CustomerMessage && (actorUserId is not null || actingAgentCredentialId is not null))
            throw new ArgumentException("A customer message cannot have a support actor.");
        if (actorUserId == Guid.Empty) throw new ArgumentException("An actor user id cannot be empty.", nameof(actorUserId));
        if (actingAgentCredentialId == Guid.Empty) throw new ArgumentException("An acting agent id cannot be empty.", nameof(actingAgentCredentialId));
        if (kind is ConversationEntryKind.PublicReply or ConversationEntryKind.InternalNote && actorUserId is null)
            throw new ArgumentException("A support-authored entry requires a user.");
        if (actingAgentCredentialId is not null && actorUserId is null)
            throw new ArgumentException("An acting agent requires its responsible user.");

        Id = id;
        TicketId = ticketId;
        Sequence = sequence;
        Kind = kind;
        Body = body.Trim();
        ActorUserId = actorUserId;
        ActingAgentCredentialId = actingAgentCredentialId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid TicketId { get; private set; }
    public int Sequence { get; private set; }
    public ConversationEntryKind Kind { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public Guid? ActorUserId { get; private set; }
    public Guid? ActingAgentCredentialId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public bool IsPublic => Kind is ConversationEntryKind.CustomerMessage or ConversationEntryKind.PublicReply;
}
