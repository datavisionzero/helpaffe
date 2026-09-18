using System.Text.Json;

namespace Helpaffe.Sdk;

/// <summary>Input for creating a support ticket for an authenticated product user.</summary>
public sealed record CreateProductTicket(
    string ExternalUserId,
    string Name,
    string Email,
    string Subject,
    string Message,
    JsonElement? Context = null);

/// <summary>One page of an end user's tickets.</summary>
public sealed record ProductTicketPage(
    IReadOnlyList<ProductTicketSummary> Items,
    string? NextCursor);

/// <summary>End-user-safe ticket details.</summary>
public sealed record ProductTicket(
    ProductTicketSummary Summary,
    ProductRequester Requester,
    JsonElement? Context,
    IReadOnlyList<PublicConversationEntry> Conversation);

/// <summary>Ticket fields exposed to the integrating product.</summary>
public sealed record ProductTicketSummary(
    Guid Id,
    string Number,
    string Subject,
    ProductTicketStatus Status,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastCustomerReplyAt);

/// <summary>The product-owned requester identity snapshot stored on a ticket.</summary>
public sealed record ProductRequester(
    string ExternalUserId,
    string Name,
    string Email);

/// <summary>A customer message or public support reply.</summary>
public sealed record PublicConversationEntry
{
    /// <summary>Creates one public entry; omitted attachments remain compatible with pre-attachment servers.</summary>
    public PublicConversationEntry(
        Guid id,
        int sequence,
        PublicConversationEntryKind kind,
        string body,
        DateTimeOffset createdAt,
        IReadOnlyList<ProductAttachment>? attachments = null)
    {
        Id = id;
        Sequence = sequence;
        Kind = kind;
        Body = body;
        CreatedAt = createdAt;
        Attachments = attachments ?? [];
    }

    /// <summary>The conversation entry id.</summary>
    public Guid Id { get; init; }
    /// <summary>The one-based chronological sequence.</summary>
    public int Sequence { get; init; }
    /// <summary>The public entry kind.</summary>
    public PublicConversationEntryKind Kind { get; init; }
    /// <summary>The message body.</summary>
    public string Body { get; init; }
    /// <summary>When the entry was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Public attachments on this entry.</summary>
    public IReadOnlyList<ProductAttachment> Attachments { get; init; }
}

/// <summary>Metadata for one immutable public conversation attachment.</summary>
public sealed record ProductAttachment(
    Guid Id,
    string FileName,
    string MediaType,
    long Size,
    DateTimeOffset CreatedAt);

/// <summary>A caller-owned readable stream to attach to a new customer message.</summary>
public sealed record ProductAttachmentUpload(
    string FileName,
    string MediaType,
    Stream Content);

/// <summary>A streamed attachment response. Dispose it after consuming <see cref="Content"/>.</summary>
public sealed class ProductAttachmentDownload : IDisposable, IAsyncDisposable
{
    private readonly HttpResponseMessage _response;

    internal ProductAttachmentDownload(
        HttpResponseMessage response,
        Stream content,
        string fileName,
        string mediaType,
        long? size)
    {
        _response = response;
        Content = content;
        FileName = fileName;
        MediaType = mediaType;
        Size = size;
    }

    /// <summary>The response stream. It is not buffered by the SDK.</summary>
    public Stream Content { get; }
    /// <summary>The original safe file name.</summary>
    public string FileName { get; }
    /// <summary>The verified media type.</summary>
    public string MediaType { get; }
    /// <summary>The response content length when supplied by the server.</summary>
    public long? Size { get; }

    /// <inheritdoc />
    public void Dispose() => _response.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The fixed ticket states exposed to a product integration.</summary>
public enum ProductTicketStatus
{
    /// <summary>The ticket needs support attention.</summary>
    Open,
    /// <summary>Support is actively working on the ticket.</summary>
    InProgress,
    /// <summary>Support is waiting for the end user.</summary>
    WaitingForCustomer,
    /// <summary>Support considers the ticket resolved.</summary>
    Resolved,
}

/// <summary>The public entry kinds visible through the Product API.</summary>
public enum PublicConversationEntryKind
{
    /// <summary>A message from the product's end user.</summary>
    CustomerMessage,
    /// <summary>A public reply from support.</summary>
    PublicReply,
}
