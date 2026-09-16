using Helpaffe.Domain.Identity;

namespace Helpaffe.Infrastructure.Identity;

public sealed class UserRecord
{
    public Guid Id { get; init; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string Name { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ProjectRecord
{
    public Guid Id { get; init; }
    public required string Key { get; set; }
    public required string Name { get; set; }
}

public sealed class UserProjectAccessRecord
{
    public Guid UserId { get; init; }
    public Guid ProjectId { get; init; }
}

public sealed class BrowserSessionRecord
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public required string TokenHash { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}
