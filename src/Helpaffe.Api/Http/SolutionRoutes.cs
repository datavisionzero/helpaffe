using System.Security.Cryptography;
using System.Text;
using Helpaffe.Domain.Solutions;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Http;

public static class SolutionRoutes
{
    public static IEndpointRouteBuilder MapSolutions(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/backoffice/projects/{projectId:guid}/solutions");
        group.MapGet("/", ListSolutions);
        group.MapPost("/", CreateSolution);
        group.MapGet("/{key}", GetSolution);
        group.MapPut("/{key}", UpdateSolution);
        group.MapDelete("/{key}", DeleteSolution);
        return endpoints;
    }

    private static async Task<IResult> ListSolutions(
        Guid projectId,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        string? search = null,
        int limit = 50,
        string? cursor = null)
    {
        if (limit is < 1 or > 100) return Problem(400, "validation", "Limit must be between 1 and 100.");
        if (search?.Length > 200) return Problem(400, "validation", "Search cannot exceed 200 characters.");

        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        if (!await ProjectIsVisible(projectId, context, database)) return ProjectNotFound();

        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var signature = CursorSignature(context.Actor()!, projectId, normalizedSearch);
        if (!TryCursor(cursor, signature, out var cursorUpdatedAt, out var cursorId))
            return Problem(400, "cursor-invalid", "The cursor does not belong to this solution query.");

        var query = database.SolutionArticles.AsNoTracking().Where(value => value.ProjectId == projectId);
        if (normalizedSearch is not null)
        {
            query = query.Where(value => EF.Functions.ToTsVector("simple", value.Title + " " + value.Markdown)
                .Matches(EF.Functions.WebSearchToTsQuery("simple", normalizedSearch)));
        }
        if (cursorUpdatedAt is not null)
            query = query.Where(value => value.UpdatedAt < cursorUpdatedAt ||
                value.UpdatedAt == cursorUpdatedAt && value.Id.CompareTo(cursorId) > 0);

        var articles = await query.OrderByDescending(value => value.UpdatedAt).ThenBy(value => value.Id)
            .Take(limit + 1)
            .ToListAsync(context.RequestAborted);
        var hasMore = articles.Count > limit;
        if (hasMore) articles.RemoveAt(articles.Count - 1);
        return Results.Ok(new
        {
            items = articles.Select(SummaryShape),
            next_cursor = hasMore ? EncodeCursor(articles[^1], signature) : null,
        });
    }

    private static async Task<IResult> GetSolution(
        Guid projectId,
        string key,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var article = await LoadVisibleSolution(projectId, key, context, database, asTracking: false);
        if (article is null) return NotFound();
        context.Response.Headers.ETag = ETag(article.Version);
        return Results.Ok(DetailShape(article));
    }

    private static async Task<IResult> CreateSolution(
        Guid projectId,
        CreateSolutionRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        if (!await ProjectIsVisible(projectId, context, database)) return ProjectNotFound();

        SolutionArticle article;
        try
        {
            article = SolutionArticle.Create(
                Guid.NewGuid(), projectId, request.Key, request.Title, request.Markdown, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "validation", exception.Message);
        }

        database.SolutionArticles.Add(article);
        try
        {
            await database.SaveChangesAsync(context.RequestAborted);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            if (await database.SolutionArticles.AsNoTracking()
                    .AnyAsync(value => value.ProjectId == projectId && value.Key == article.Key, context.RequestAborted))
                return Problem(409, "solution-key-conflict", "A solution article with this key already exists in the project.");
            throw;
        }

        context.Response.Headers.ETag = ETag(article.Version);
        return Results.Created(
            $"/api/backoffice/projects/{projectId}/solutions/{Uri.EscapeDataString(article.Key)}",
            DetailShape(article));
    }

    private static async Task<IResult> UpdateSolution(
        Guid projectId,
        string key,
        UpdateSolutionRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var article = await LoadVisibleSolution(projectId, key, context, database, asTracking: true);
        if (article is null) return NotFound();
        if (!ExpectedVersion(context, article.Version, out var versionError)) return versionError!;

        try
        {
            article.Update(request.Title, request.Markdown, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "validation", exception.Message);
        }

        var saveError = await SaveSolution(database, article.Id, context.RequestAborted);
        if (saveError is not null) return saveError;
        context.Response.Headers.ETag = ETag(article.Version);
        return Results.Ok(DetailShape(article));
    }

    private static async Task<IResult> DeleteSolution(
        Guid projectId,
        string key,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var article = await LoadVisibleSolution(projectId, key, context, database, asTracking: true);
        if (article is null) return NotFound();
        if (!ExpectedVersion(context, article.Version, out var versionError)) return versionError!;

        database.SolutionArticles.Remove(article);
        var saveError = await SaveSolution(database, article.Id, context.RequestAborted);
        return saveError ?? Results.NoContent();
    }

    private static async Task<SolutionArticle?> LoadVisibleSolution(
        Guid projectId,
        string key,
        HttpContext context,
        HelpaffeDbContext database,
        bool asTracking)
    {
        if (!await ProjectIsVisible(projectId, context, database)) return null;
        var normalizedKey = key.Trim().ToLowerInvariant();
        var query = asTracking ? database.SolutionArticles : database.SolutionArticles.AsNoTracking();
        return await query.SingleOrDefaultAsync(
            value => value.ProjectId == projectId && value.Key == normalizedKey,
            context.RequestAborted);
    }

    private static Task<bool> ProjectIsVisible(
        Guid projectId,
        HttpContext context,
        HelpaffeDbContext database) =>
        context.VisibleProjects(database).AnyAsync(value => value.Id == projectId, context.RequestAborted);

    private static bool ExpectedVersion(HttpContext context, int currentVersion, out IResult? error)
    {
        var value = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = Problem(428, "version-required", "If-Match with the last-read solution version is required.");
            return false;
        }
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' ||
            !int.TryParse(value[1..^1], out var expected) || expected < 1)
        {
            error = Problem(400, "validation", "If-Match must contain one quoted positive solution version.");
            return false;
        }
        if (expected != currentVersion)
        {
            error = Stale(currentVersion);
            return false;
        }
        error = null;
        return true;
    }

    private static async Task<IResult?> SaveSolution(
        HelpaffeDbContext database,
        Guid articleId,
        CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            var currentVersion = await database.SolutionArticles.AsNoTracking()
                .Where(value => value.Id == articleId)
                .Select(value => (int?)value.Version)
                .SingleOrDefaultAsync(cancellationToken);
            return currentVersion is null ? NotFound() : Stale(currentVersion.Value);
        }
    }

    private static object SummaryShape(SolutionArticle article) => new
    {
        article.Id,
        project_id = article.ProjectId,
        key = article.Key,
        title = article.Title,
        version = article.Version,
        created_at = article.CreatedAt,
        updated_at = article.UpdatedAt,
    };

    private static object DetailShape(SolutionArticle article) => new
    {
        article.Id,
        project_id = article.ProjectId,
        key = article.Key,
        title = article.Title,
        markdown = article.Markdown,
        version = article.Version,
        created_at = article.CreatedAt,
        updated_at = article.UpdatedAt,
    };

    private static string CursorSignature(BackofficeActor actor, Guid projectId, string? search)
    {
        var value = $"solutions:{actor.User.Id:N}:{actor.Agent?.Id:N}:{projectId:N}:{search}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }

    private static string EncodeCursor(SolutionArticle article, string signature)
    {
        var value = $"{article.UpdatedAt.UtcTicks}:{article.Id:N}:{signature}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryCursor(string? cursor, string signature, out DateTimeOffset? updatedAt, out Guid id)
    {
        updatedAt = null;
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split(':');
            if (parts.Length != 3 || parts[2] != signature || !long.TryParse(parts[0], out var ticks) ||
                !Guid.TryParseExact(parts[1], "N", out id))
                return false;
            updatedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string ETag(int version) => $"\"{version}\"";
    private static IResult ProjectNotFound() => Problem(404, "not-found", "The project was not found in the caller's project scope.");
    private static IResult NotFound() => Problem(404, "not-found", "The solution article was not found in the caller's project scope.");
    private static IResult Stale(int currentVersion) => Results.Json(new
    {
        type = "/problems/stale",
        title = "The solution article changed",
        status = 412,
        detail = "Read the current solution article and retry with its version.",
        current_version = currentVersion,
    }, contentType: "application/problem+json", statusCode: 412);
    private static IResult Problem(int status, string code, string detail) => Results.Json(new
    {
        type = $"/problems/{code}",
        title = code.Replace('-', ' '),
        status,
        detail,
    }, contentType: "application/problem+json", statusCode: status);

    private sealed record CreateSolutionRequest(string Key, string Title, string Markdown);
    private sealed record UpdateSolutionRequest(string Title, string Markdown);
}
