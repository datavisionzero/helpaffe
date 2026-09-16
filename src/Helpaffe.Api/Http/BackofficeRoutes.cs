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
        group.MapGet("/me", (HttpContext context) => Results.Ok(ActorShape(context.Actor()!)));
        group.MapGet("/projects", ListProjects);
        group.MapPost("/projects", CreateProject);
        group.MapGet("/users", ListUsers);
        group.MapPost("/users", CreateUser);
        group.MapPatch("/users/{id:guid}", UpdateUser);
        group.MapPut("/users/{userId:guid}/projects/{projectId:guid}", GrantProject);
        group.MapDelete("/users/{userId:guid}/projects/{projectId:guid}", RevokeProject);
        group.MapGet("/agents", ListAgents);
        group.MapPost("/agents", CreateAgent);
        group.MapDelete("/agents/{id:guid}", RevokeAgent);
        group.MapGet("/product-keys", ListProductKeys);
        group.MapPost("/projects/{projectId:guid}/product-keys", CreateProductKey);
        group.MapDelete("/product-keys/{id:guid}", RevokeProductKey);

        return endpoints;
    }

    private static async Task<IResult> SignIn(
        SignInRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return ApiProblem(401, "unauthenticated", "The email address or password is incorrect.");
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
            TokenHash = AccessToken.Hash(token),
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
        if (!context.IsHuman()) return HumanOnly();
        if (context.Request.Cookies.TryGetValue(BackofficeSecurity.CookieName, out var token))
        {
            await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
            var hash = AccessToken.Hash(token);
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
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var query = context.VisibleProjects(database);

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
        if (!context.IsHumanAdministrator()) return Forbidden();
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
        if (!context.IsHumanAdministrator()) return Forbidden();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email) || request.Password is null || request.Password.Length < 12)
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
        if (!context.IsHumanAdministrator()) return Forbidden();
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
        if (!context.IsHumanAdministrator()) return Forbidden();
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

    private static async Task<IResult> ListAgents(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsHuman()) return HumanOnly();
        var current = context.User()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var query = database.AgentCredentials.AsNoTracking();
        if (current.Role is not UserRole.Administrator)
            query = query.Where(value => value.UserId == current.Id);
        var credentials = await query.OrderBy(value => value.Name).ToListAsync(context.RequestAborted);
        var userIds = credentials.Select(value => value.UserId).Distinct().ToArray();
        var users = await database.Users.AsNoTracking().Where(value => userIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, context.RequestAborted);
        var credentialIds = credentials.Select(value => value.Id).ToArray();
        var access = await database.AgentProjectAccess.AsNoTracking()
            .Where(value => credentialIds.Contains(value.AgentCredentialId))
            .ToListAsync(context.RequestAborted);
        return Results.Ok(credentials.Select(value => AgentShape(value, users[value.UserId], access
            .Where(item => item.AgentCredentialId == value.Id)
            .Select(item => item.ProjectId))));
    }

    private static async Task<IResult> CreateAgent(
        CreateAgentRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsHuman()) return HumanOnly();
        if (string.IsNullOrWhiteSpace(request.Name))
            return ApiProblem(400, "validation", "An agent name is required.");
        var current = context.User()!;
        var targetUserId = request.UserId ?? current.Id;
        if (current.Role is not UserRole.Administrator && targetUserId != current.Id) return Forbidden();

        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var owner = await database.Users.SingleOrDefaultAsync(
            value => value.Id == targetUserId,
            context.RequestAborted);
        if (owner is null) return ApiProblem(404, "not-found", "The user was not found.");
        if (!owner.IsActive) return ApiProblem(409, "inactive-user", "An agent cannot be created for an inactive user.");

        var requestedProjectIds = (request.ProjectIds ?? []).Distinct().ToArray();
        if (!request.AllProjects)
        {
            IQueryable<Guid> allowedProjectIds = database.Projects.Select(value => value.Id);
            if (owner.Role is not UserRole.Administrator)
                allowedProjectIds = database.UserProjectAccess.Where(value => value.UserId == owner.Id).Select(value => value.ProjectId);
            var allowed = await allowedProjectIds.CountAsync(
                value => requestedProjectIds.Contains(value),
                context.RequestAborted);
            if (allowed != requestedProjectIds.Length)
                return ApiProblem(403, "forbidden", "The agent scope must be a subset of its user's current project access.");
        }

        var generated = AccessToken.Create("a");
        var credential = new AgentCredentialRecord
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Name = request.Name.Trim(),
            TokenHash = generated.Hash,
            TokenPrefix = generated.Prefix,
            AllProjects = request.AllProjects,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        database.AgentCredentials.Add(credential);
        if (!request.AllProjects)
        {
            database.AgentProjectAccess.AddRange(requestedProjectIds.Select(projectId => new AgentProjectAccessRecord
            {
                AgentCredentialId = credential.Id,
                ProjectId = projectId,
            }));
        }
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.Created($"/api/backoffice/agents/{credential.Id}", new
        {
            credential = AgentShape(credential, owner, requestedProjectIds),
            token = generated.Token,
        });
    }

    private static async Task<IResult> RevokeAgent(
        Guid id,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsHuman()) return HumanOnly();
        var current = context.User()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var credential = await database.AgentCredentials.FindAsync([id], context.RequestAborted);
        if (credential is null || current.Role is not UserRole.Administrator && credential.UserId != current.Id)
            return ApiProblem(404, "not-found", "The agent credential was not found.");
        credential.RevokedAt ??= DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ListProductKeys(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsHumanAdministrator()) return Forbidden();
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var projects = await database.Projects.AsNoTracking().ToDictionaryAsync(value => value.Id, context.RequestAborted);
        var credentials = await database.ProductApiKeys.AsNoTracking().OrderBy(value => value.Name).ToListAsync(context.RequestAborted);
        return Results.Ok(credentials.Select(value => ProductKeyShape(value, projects[value.ProjectId])));
    }

    private static async Task<IResult> CreateProductKey(
        Guid projectId,
        CreateProductKeyRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsHumanAdministrator()) return Forbidden();
        if (string.IsNullOrWhiteSpace(request.Name))
            return ApiProblem(400, "validation", "A product API key name is required.");
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var project = await database.Projects.FindAsync([projectId], context.RequestAborted);
        if (project is null) return ApiProblem(404, "not-found", "The project was not found.");
        var generated = AccessToken.Create("p");
        var credential = new ProductApiKeyRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = request.Name.Trim(),
            TokenHash = generated.Hash,
            TokenPrefix = generated.Prefix,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        database.ProductApiKeys.Add(credential);
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.Created($"/api/backoffice/product-keys/{credential.Id}", new
        {
            credential = ProductKeyShape(credential, project),
            token = generated.Token,
        });
    }

    private static async Task<IResult> RevokeProductKey(
        Guid id,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsHumanAdministrator()) return Forbidden();
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var credential = await database.ProductApiKeys.FindAsync([id], context.RequestAborted);
        if (credential is null) return ApiProblem(404, "not-found", "The product API key was not found.");
        credential.RevokedAt ??= DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.NoContent();
    }

    private static object ActorShape(BackofficeActor actor) => new
    {
        actor.User.Id,
        actor.User.Email,
        actor.User.Name,
        role = actor.User.Role is UserRole.Administrator ? "administrator" : "support",
        actor_kind = actor.Kind is BackofficeActorKind.Human ? "human" : "agent",
        agent = actor.Agent is null ? null : new { actor.Agent.Id, actor.Agent.Name },
    };

    private static object AgentShape(AgentCredentialRecord credential, UserRecord owner, IEnumerable<Guid> projectIds) => new
    {
        credential.Id,
        user_id = owner.Id,
        user_name = owner.Name,
        credential.Name,
        token_prefix = credential.TokenPrefix,
        all_projects = credential.AllProjects,
        project_ids = projectIds,
        is_active = credential.RevokedAt is null,
        created_at = credential.CreatedAt,
    };

    private static object ProductKeyShape(ProductApiKeyRecord credential, ProjectRecord project) => new
    {
        credential.Id,
        project_id = project.Id,
        project_name = project.Name,
        credential.Name,
        token_prefix = credential.TokenPrefix,
        is_active = credential.RevokedAt is null,
        created_at = credential.CreatedAt,
    };

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

    private static IResult HumanOnly() => ApiProblem(403, "forbidden", "This action requires a signed-in human user.");

    private static IResult ApiProblem(int status, string code, string detail) => Results.Json(new
    {
        type = $"/problems/{code}", title = code.Replace('-', ' '), status, detail,
    }, statusCode: status);

    private sealed record SignInRequest(string Email, string Password);
    private sealed record CreateProjectRequest(string Key, string Name);
    private sealed record CreateUserRequest(string Name, string Email, string Password, string Role);
    private sealed record UpdateUserRequest(string? Role, bool? IsActive);
    private sealed record CreateAgentRequest(string Name, Guid? UserId, bool AllProjects, Guid[]? ProjectIds);
    private sealed record CreateProductKeyRequest(string Name);
}
