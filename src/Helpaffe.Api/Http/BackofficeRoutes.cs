using System.Security.Cryptography;
using Helpaffe.Domain.Identity;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Http;

public static class BackofficeRoutes
{
    public static IEndpointRouteBuilder MapBackoffice(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/backoffice");

        group.MapPost("/session", SignIn);
        group.MapDelete("/session", SignOut);
        group.MapGet("/me", (HttpContext context) => Results.Ok(UserShape(context.User()!)));
        group.MapGet("/projects", ListProjects);
        group.MapPost("/projects", CreateProject);
        group.MapGet("/users", ListUsers);
        group.MapPost("/users", CreateUser);
        group.MapPatch("/users/{id:guid}", UpdateUser);
        group.MapPut("/users/{userId:guid}/projects/{projectId:guid}", GrantProject);
        group.MapDelete("/users/{userId:guid}/projects/{projectId:guid}", RevokeProject);

        return endpoints;
    }

    private static async Task<IResult> SignIn(
        SignInRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var normalized = request.Email.Trim().ToUpperInvariant();
        var user = await database.Users.SingleOrDefaultAsync(
            value => value.NormalizedEmail == normalized,
            context.RequestAborted);
        if (user is null || !user.IsActive || !PasswordHash.Verify(request.Password, user.PasswordHash))
        {
            return ApiProblem(401, "unauthenticated", "The email address or password is incorrect.");
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        database.BrowserSessions.Add(new BrowserSessionRecord
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = BackofficeSecurity.HashToken(token),
            ExpiresAt = expires,
        });
        await database.SaveChangesAsync(context.RequestAborted);
        context.Response.Cookies.Append(BackofficeSecurity.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = !context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase),
            SameSite = SameSiteMode.Strict,
            Expires = expires,
            Path = "/",
        });
        return Results.Ok(UserShape(user));
    }

    private static async Task<IResult> SignOut(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (context.Request.Cookies.TryGetValue(BackofficeSecurity.CookieName, out var token))
        {
            await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
            var hash = BackofficeSecurity.HashToken(token);
            await database.BrowserSessions.Where(value => value.TokenHash == hash)
                .ExecuteDeleteAsync(context.RequestAborted);
        }

        context.Response.Cookies.Delete(BackofficeSecurity.CookieName);
        return Results.NoContent();
    }

    private static async Task<IResult> ListProjects(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        var current = context.User()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var query = database.Projects.AsNoTracking();
        if (current.Role is not UserRole.Administrator)
        {
            query = query.Where(project => database.UserProjectAccess.Any(
                access => access.UserId == current.Id && access.ProjectId == project.Id));
        }

        return Results.Ok(await query.OrderBy(project => project.Name)
            .Select(project => new { project.Id, project.Key, project.Name })
            .ToListAsync(context.RequestAborted));
    }

    private static async Task<IResult> CreateProject(
        CreateProjectRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsAdministrator()) return Forbidden();
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.Name))
            return ApiProblem(400, "validation", "Project key and name are required.");

        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var project = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            Key = request.Key.Trim().ToUpperInvariant(),
            Name = request.Name.Trim(),
        };
        database.Projects.Add(project);
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.Created($"/api/backoffice/projects/{project.Id}", new { project.Id, project.Key, project.Name });
    }

    private static async Task<IResult> ListUsers(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsAdministrator()) return Forbidden();
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var users = await database.Users.AsNoTracking().OrderBy(user => user.Name).ToListAsync(context.RequestAborted);
        var access = await database.UserProjectAccess.AsNoTracking().ToListAsync(context.RequestAborted);
        return Results.Ok(users.Select(user => UserShape(user, access
            .Where(item => item.UserId == user.Id)
            .Select(item => item.ProjectId))));
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsAdministrator()) return Forbidden();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email) || request.Password.Length < 12)
            return ApiProblem(400, "validation", "Name, email, and a password of at least 12 characters are required.");
        if (!TryRole(request.Role, out var role)) return ApiProblem(400, "validation", "Role must be administrator or support.");

        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var user = new UserRecord
        {
            Id = Guid.NewGuid(),
            Email = request.Email.Trim(),
            NormalizedEmail = request.Email.Trim().ToUpperInvariant(),
            Name = request.Name.Trim(),
            PasswordHash = PasswordHash.Create(request.Password),
            Role = role,
        };
        database.Users.Add(user);
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.Created($"/api/backoffice/users/{user.Id}", UserShape(user));
    }

    private static async Task<IResult> UpdateUser(
        Guid id,
        UpdateUserRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsAdministrator()) return Forbidden();
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var user = await database.Users.FindAsync([id], context.RequestAborted);
        if (user is null) return ApiProblem(404, "not-found", "The user was not found.");

        var role = user.Role;
        if (request.Role is not null && !TryRole(request.Role, out role))
            return ApiProblem(400, "validation", "Role must be administrator or support.");
        var active = request.IsActive ?? user.IsActive;
        if (user.Role is UserRole.Administrator && (!active || role is not UserRole.Administrator) &&
            await database.Users.CountAsync(value => value.IsActive && value.Role == UserRole.Administrator, context.RequestAborted) == 1)
            return ApiProblem(409, "last-administrator", "At least one active administrator must remain.");

        user.Role = role;
        user.IsActive = active;
        if (!active)
            await database.BrowserSessions.Where(value => value.UserId == user.Id).ExecuteDeleteAsync(context.RequestAborted);
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.Ok(UserShape(user));
    }

    private static Task<IResult> GrantProject(Guid userId, Guid projectId, HttpContext context, IDbContextFactory<HelpaffeDbContext> factory) =>
        ChangeProjectAccess(userId, projectId, true, context, factory);

    private static Task<IResult> RevokeProject(Guid userId, Guid projectId, HttpContext context, IDbContextFactory<HelpaffeDbContext> factory) =>
        ChangeProjectAccess(userId, projectId, false, context, factory);

    private static async Task<IResult> ChangeProjectAccess(
        Guid userId,
        Guid projectId,
        bool grant,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsAdministrator()) return Forbidden();
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        if (!await database.Users.AnyAsync(value => value.Id == userId, context.RequestAborted) ||
            !await database.Projects.AnyAsync(value => value.Id == projectId, context.RequestAborted))
            return ApiProblem(404, "not-found", "The user or project was not found.");
        var existing = await database.UserProjectAccess.FindAsync([userId, projectId], context.RequestAborted);
        if (grant && existing is null)
            database.UserProjectAccess.Add(new UserProjectAccessRecord { UserId = userId, ProjectId = projectId });
        if (!grant && existing is not null)
            database.UserProjectAccess.Remove(existing);
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.NoContent();
    }

    private static object UserShape(UserRecord user, IEnumerable<Guid>? projectIds = null) => new
    {
        user.Id,
        user.Email,
        user.Name,
        role = user.Role is UserRole.Administrator ? "administrator" : "support",
        is_active = user.IsActive,
        project_ids = projectIds ?? [],
    };

    private static bool TryRole(string value, out UserRole role) =>
        Enum.TryParse(value, true, out role) && Enum.IsDefined(role);

    private static IResult Forbidden() => ApiProblem(403, "forbidden", "Administrator access is required.");

    private static IResult ApiProblem(int status, string code, string detail) => Results.Json(new
    {
        type = $"/problems/{code}", title = code.Replace('-', ' '), status, detail,
    }, statusCode: status);

    private sealed record SignInRequest(string Email, string Password);
    private sealed record CreateProjectRequest(string Key, string Name);
    private sealed record CreateUserRequest(string Name, string Email, string Password, string Role);
    private sealed record UpdateUserRequest(string? Role, bool? IsActive);
}
