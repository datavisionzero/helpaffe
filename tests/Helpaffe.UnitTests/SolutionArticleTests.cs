using Helpaffe.Domain.Solutions;

namespace Helpaffe.UnitTests;

public sealed class SolutionArticleTests
{
    [Fact]
    public void Article_key_is_stable_while_content_changes_advance_the_version()
    {
        var createdAt = new DateTimeOffset(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);
        var article = SolutionArticle.Create(
            Guid.NewGuid(), Guid.NewGuid(), "Postgres-Restart", " Restart PostgreSQL ", "Run the playbook.", createdAt);

        article.Update("Restart PostgreSQL safely", "Run the tested playbook.", createdAt.AddMinutes(1));

        Assert.Equal("postgres-restart", article.Key);
        Assert.Equal("Restart PostgreSQL safely", article.Title);
        Assert.Equal(2, article.Version);
        Assert.Equal(createdAt.AddMinutes(1), article.UpdatedAt);
    }

    [Theory]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("not_allowed")]
    [InlineData("two words")]
    public void Invalid_article_keys_are_rejected(string key)
    {
        Assert.Throws<ArgumentException>(() => SolutionArticle.Create(
            Guid.NewGuid(), Guid.NewGuid(), key, "Title", "Body", DateTimeOffset.UtcNow));
    }
}
