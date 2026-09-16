using Helpaffe.Domain.Identity;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Http;

public enum BackofficeActorKind
{
    Human,
    Agent,
}

public sealed record BackofficeActor(
    UserRecord User,
    BackofficeActorKind Kind,
    AgentCredentialRecord? Agent = null);

public static class BackofficeSecurity
{
    public const string CookieName = "helpaffe_session";
    private const string ActorItem = "helpaffe.actor";

    public static BackofficeActor? Actor(this HttpContext context) =>
        context.Items.TryGetValue(ActorItem, out var value) ? value as BackofficeActor : null;

    public static UserRecord? User(this HttpContext context) => context.Actor()?.User;

    public static bool IsAdministrator(this HttpContext context) =>
        context.User()?.Role is UserRole.Administrator;

    public static bool IsHuman(this HttpContext context) =>
        context.Actor()?.Kind is BackofficeActorKind.Human;

    public static bool IsHumanAdministrator(this HttpContext context) =>
        context.IsHuman() && context.IsAdministrator();

    public static async Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/api/backoffice") ||
            context.Request.Path == "/api/backoffice/version" ||
            context.Request.Path == "/api/backoffice/session" && context.Request.Method == "POST")
        {
            await next(context);
            return;
        }

        var factory = context.RequestServices.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>();
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var actor = await AuthenticateBearer(context, database) ?? await AuthenticateBrowser(context, database);
        if (actor is null || !actor.User.IsActive)
        {
            context.Response.Cookies.Delete(CookieName);
            await Problem(context, 401, "unauthenticated", "The credential is absent, expired, or revoked.");
            return;
        }

        if (actor.Kind is BackofficeActorKind.Human &&
            context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
            context.Request.Headers["X-Helpaffe-CSRF"] != "1")
        {
            await Problem(context, 403, "csrf", "The CSRF header is required.");
            return;
        }

        context.Items[ActorItem] = actor;
        await next(context);
    }

    public static IQueryable<ProjectRecord> VisibleProjects(
        this HttpContext context,
        HelpaffeDbContext database)
    {
        var actor = context.Actor()!;
        IQueryable<ProjectRecord> projects = database.Projects.AsNoTracking();
        if (actor.User.Role is not UserRole.Administrator)
        {
            projects = projects.Where(project => database.UserProjectAccess.Any(
                access => access.UserId == actor.User.Id && access.ProjectId == project.Id));
        }

        if (actor.Agent is { AllProjects: false } agent)
        {
            var scopedIds = database.AgentProjectAccess
                .Where(access => access.AgentCredentialId == agent.Id)
                .Select(access => access.ProjectId);
            projects = projects.Where(project => scopedIds.Contains(project.Id));
        }

        return projects;
    }

    private static async Task<BackofficeActor?> AuthenticateBearer(HttpContext context, HelpaffeDbContext database)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer hfa_", StringComparison.Ordinal)) return null;
        var hash = AccessToken.Hash(authorization[7..]);
        var agent = await database.AgentCredentials.AsNoTracking().SingleOrDefaultAsync(
            value => value.TokenHash == hash && value.RevokedAt == null,
            context.RequestAborted);
        if (agent is null) return null;
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == agent.UserId,
            context.RequestAborted);
        return user is null ? null : new BackofficeActor(user, BackofficeActorKind.Agent, agent);
    }

    private static async Task<BackofficeActor?> AuthenticateBrowser(HttpContext context, HelpaffeDbContext database)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token)) return null;
        var hash = AccessToken.Hash(token);
        var session = await database.BrowserSessions.AsNoTracking().SingleOrDefaultAsync(
            value => value.TokenHash == hash && value.ExpiresAt > DateTimeOffset.UtcNow,
            context.RequestAborted);
        if (session is null) return null;
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == session.UserId,
            context.RequestAborted);
        return user is null ? null : new BackofficeActor(user, BackofficeActorKind.Human);
    }

    public static Task Problem(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(new
        {
            type = $"/problems/{code}",
            title = code.Replace('-', ' '),
            status,
            detail,
            instance = context.Request.Path.Value,
        });
    }
}
