namespace Helpaffe.Domain.Tickets;

public enum DevelopmentReferenceType
{
    Planaffe,
    GitHub,
    GitLab,
}

public sealed class DevelopmentReference
{
    private DevelopmentReference()
    {
    }

    internal DevelopmentReference(
        Guid id,
        Guid ticketId,
        int position,
        DevelopmentReferenceType type,
        string url,
        string label,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("A reference id is required.", nameof(id));
        if (ticketId == Guid.Empty) throw new ArgumentException("A ticket id is required.", nameof(ticketId));
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(parsed.Host))
            throw new ArgumentException("A development reference must use an absolute HTTPS URL.", nameof(url));
        if (url.Trim().Length > 2048) throw new ArgumentOutOfRangeException(nameof(url));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A reference label is required.", nameof(label));
        if (label.Trim().Length > 200) throw new ArgumentOutOfRangeException(nameof(label));

        Id = id;
        TicketId = ticketId;
        Position = position;
        Type = type;
        Url = url.Trim();
        Label = label.Trim();
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid TicketId { get; private set; }
    public int Position { get; private set; }
    public DevelopmentReferenceType Type { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}
