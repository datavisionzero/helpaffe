namespace Helpaffe.Domain.Tickets;

public sealed class TicketAttachment
{
    public const int MaximumFilesPerMessage = 5;
    public const long MaximumFileSize = 10 * 1024 * 1024;
    public const long MaximumTotalSize = 25 * 1024 * 1024;

    private TicketAttachment()
    {
    }

    internal TicketAttachment(
        Guid id,
        Guid projectId,
        Guid ticketId,
        Guid conversationEntryId,
        string fileName,
        string mediaType,
        long size,
        string storageKey,
        bool isPublic,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("An attachment id is required.", nameof(id));
        if (projectId == Guid.Empty) throw new ArgumentException("A project id is required.", nameof(projectId));
        if (ticketId == Guid.Empty) throw new ArgumentException("A ticket id is required.", nameof(ticketId));
        if (conversationEntryId == Guid.Empty) throw new ArgumentException("A conversation entry id is required.", nameof(conversationEntryId));
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
            throw new ArgumentException("A file name with at most 255 characters is required.", nameof(fileName));
        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("The file name cannot contain a path.", nameof(fileName));
        if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > 100)
            throw new ArgumentException("A media type with at most 100 characters is required.", nameof(mediaType));
        if (size is < 1 or > MaximumFileSize)
            throw new ArgumentOutOfRangeException(nameof(size), $"An attachment must contain between 1 byte and {MaximumFileSize} bytes.");
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Length > 200 ||
            storageKey.Any(value => !char.IsAsciiHexDigit(value)))
            throw new ArgumentException("A hexadecimal storage key with at most 200 characters is required.", nameof(storageKey));

        Id = id;
        ProjectId = projectId;
        TicketId = ticketId;
        ConversationEntryId = conversationEntryId;
        FileName = fileName;
        MediaType = mediaType;
        Size = size;
        StorageKey = storageKey;
        IsPublic = isPublic;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid ConversationEntryId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string MediaType { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public bool IsPublic { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
