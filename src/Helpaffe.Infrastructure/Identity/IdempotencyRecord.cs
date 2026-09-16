namespace Helpaffe.Infrastructure.Identity;

public sealed class IdempotencyRecord
{
    public required string CredentialKind { get; init; }
    public Guid CredentialId { get; init; }
    public required string Key { get; init; }
    public required string RequestHash { get; init; }
    public int ResponseStatus { get; init; }
    public required string ResponseBody { get; init; }
    public required string ResponseETag { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}
