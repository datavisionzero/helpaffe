namespace Helpaffe.Domain.Solutions;

public sealed class SolutionArticle
{
    private SolutionArticle()
    {
    }

    public static SolutionArticle Create(
        Guid id,
        Guid projectId,
        string key,
        string title,
        string markdown,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("An article id is required.", nameof(id));
        if (projectId == Guid.Empty) throw new ArgumentException("A project id is required.", nameof(projectId));

        return new SolutionArticle
        {
            Id = id,
            ProjectId = projectId,
            Key = NormalizeKey(key),
            Title = Required(title, nameof(title), 200),
            Markdown = Required(markdown, nameof(markdown), 65_536, trim: false),
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Markdown { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string title, string markdown, DateTimeOffset updatedAt)
    {
        var normalizedTitle = Required(title, nameof(title), 200);
        var normalizedMarkdown = Required(markdown, nameof(markdown), 65_536, trim: false);
        if (Title == normalizedTitle && Markdown == normalizedMarkdown) return;

        Title = normalizedTitle;
        Markdown = normalizedMarkdown;
        UpdatedAt = updatedAt;
        Version++;
    }

    private static string NormalizeKey(string value)
    {
        var key = Required(value, nameof(value), 80).ToLowerInvariant();
        if (key[0] is '-' || key[^1] is '-' || key.Any(character =>
                character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-'))
            throw new ArgumentException("An article key must contain lowercase letters, numbers, or internal hyphens.", nameof(value));
        return key;
    }

    private static string Required(string value, string parameterName, int maximumLength, bool trim = true)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", parameterName);
        var normalized = trim ? value.Trim() : value;
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The value cannot exceed {maximumLength} characters.", parameterName);
        return normalized;
    }
}
