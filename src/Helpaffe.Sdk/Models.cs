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
public sealed record PublicConversationEntry(
    Guid Id,
    int Sequence,
    PublicConversationEntryKind Kind,
    string Body,
    DateTimeOffset CreatedAt);

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
