namespace Helpaffe.Infrastructure.Identity;

public sealed class AgentCredentialRecord
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public required string Name { get; set; }
    public required string TokenHash { get; init; }
    public required string TokenPrefix { get; init; }
    public bool AllProjects { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AgentProjectAccessRecord
{
    public Guid AgentCredentialId { get; init; }
    public Guid ProjectId { get; init; }
}

public sealed class ProductApiKeyRecord
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public required string TokenHash { get; init; }
    public required string TokenPrefix { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
}
