using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpaffe.Domain.Identity;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Helpaffe.Api.Http;

public static class TicketRoutes
{
    public static IEndpointRouteBuilder MapTickets(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/backoffice");
        group.MapGet("/assignees", ListAssignees);
        group.MapGet("/tickets", ListTickets);
        group.MapPost("/tickets/next", AcquireNextTicket);
        group.MapGet("/tickets/{number}", GetTicket);
        group.MapGet("/tickets/{number}/requester-tickets", ListRequesterTickets);
        group.MapPatch("/tickets/{number}", UpdateTicket);
        group.MapPost("/tickets/{number}/replies", AddReply);
        group.MapPost("/tickets/{number}/notes", AddNote);
        group.MapGet("/projects/{projectId:guid}/support-instructions", GetSupportInstructions);
        group.MapPut("/projects/{projectId:guid}/support-instructions", UpdateSupportInstructions);
        return endpoints;
    }

    private static async Task<IResult> ListAssignees(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        Guid? project_id = null)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var visibleProjectIds = await context.VisibleProjects(database)
            .Select(value => value.Id)
            .ToArrayAsync(context.RequestAborted);
        if (project_id is { } projectId && !visibleProjectIds.Contains(projectId))
            return ProjectNotFound();

        var eligibleProjectIds = project_id is { } selectedProjectId ? [selectedProjectId] : visibleProjectIds;
        var assignees = await database.Users.AsNoTracking()
            .Where(user => eligibleProjectIds.Length > 0 && user.IsActive &&
                (user.Role == UserRole.Administrator || database.UserProjectAccess.Any(access =>
                    access.UserId == user.Id && eligibleProjectIds.Contains(access.ProjectId))))
            .OrderBy(user => user.Name)
            .Select(user => new { user.Id, user.Name })
            .ToListAsync(context.RequestAborted);
        return Results.Ok(assignees);
    }

    private static async Task<IResult> ListTickets(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        string? status = null,
        string? priority = null,
        Guid? project_id = null,
        Guid? assignee_id = null,
        bool mine = false,
        string? search = null,
        int limit = 50,
        string? cursor = null)
    {
        if (limit is < 1 or > 100) return Problem(400, "validation", "Limit must be between 1 and 100.");
        if (!TryStatus(status, out var parsedStatus)) return Problem(400, "validation", "The ticket status is invalid.");
        if (!TryPriority(priority, out var parsedPriority)) return Problem(400, "validation", "The ticket priority is invalid.");

        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var visibleProjectIds = await context.VisibleProjects(database)
            .Select(value => value.Id)
            .OrderBy(value => value)
            .ToArrayAsync(context.RequestAborted);
        var signature = CursorSignature(context.Actor()!, visibleProjectIds, status, priority, project_id, assignee_id, mine, search);
        if (!TryCursor(cursor, signature, out var cursorUpdatedAt, out var cursorId))
            return Problem(400, "cursor-invalid", "The cursor does not belong to this ticket query.");

        var query = database.Tickets.AsNoTracking().Where(value => visibleProjectIds.Contains(value.ProjectId));
        if (parsedStatus is not null) query = query.Where(value => value.Status == parsedStatus);
        if (parsedPriority is not null) query = query.Where(value => value.Priority == parsedPriority);
        if (project_id is not null) query = query.Where(value => value.ProjectId == project_id);
        var selectedAssignee = mine ? context.User()!.Id : assignee_id;
        if (selectedAssignee is not null) query = query.Where(value => value.AssigneeUserId == selectedAssignee);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchText = search.Trim();
            query = query.Where(value =>
                EF.Functions.ToTsVector("simple",
                    value.Number + " " + value.Subject + " " + value.RequesterName + " " + value.RequesterEmail)
                    .Matches(EF.Functions.WebSearchToTsQuery("simple", searchText)) ||
                value.Conversation.Any(entry => EF.Functions.ToTsVector("simple", entry.Body)
                    .Matches(EF.Functions.WebSearchToTsQuery("simple", searchText))));
        }
        if (cursorUpdatedAt is not null)
            query = query.Where(value => value.UpdatedAt < cursorUpdatedAt || value.UpdatedAt == cursorUpdatedAt && value.Id.CompareTo(cursorId) > 0);

        var tickets = await query.OrderByDescending(value => value.UpdatedAt).ThenBy(value => value.Id)
            .Take(limit + 1)
            .ToListAsync(context.RequestAborted);
        var hasMore = tickets.Count > limit;
        if (hasMore) tickets.RemoveAt(tickets.Count - 1);
        var projectIds = tickets.Select(value => value.ProjectId).Distinct().ToArray();
        var projects = await database.Projects.AsNoTracking().Where(value => projectIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, context.RequestAborted);
        var assigneeIds = tickets.Where(value => value.AssigneeUserId is not null).Select(value => value.AssigneeUserId!.Value).Distinct().ToArray();
        var users = await database.Users.AsNoTracking().Where(value => assigneeIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, context.RequestAborted);
        return Results.Ok(new
        {
            items = tickets.Select(value => TicketSummary(value, projects[value.ProjectId],
                value.AssigneeUserId is { } id ? users.GetValueOrDefault(id) : null)),
            next_cursor = hasMore ? EncodeCursor(tickets[^1], signature) : null,
        });
    }

    private static async Task<IResult> GetTicket(
        string number,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        context.Response.Headers.ETag = ETag(ticket.Version);
        return Results.Ok(await TicketDetail(ticket, database, context.RequestAborted));
    }

    private static async Task<IResult> ListRequesterTickets(
        string number,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        string? status = null,
        int limit = 50,
        string? cursor = null)
    {
        if (limit is < 1 or > 100) return Problem(400, "validation", "Limit must be between 1 and 100.");
        if (!TryStatus(status, out var parsedStatus)) return Problem(400, "validation", "The ticket status is invalid.");

        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var source = await LoadVisibleTicket(number, context, database);
        if (source is null) return NotFound();
        var signature = RequesterTicketsCursorSignature(context.Actor()!, source, status);
        if (!TryCursor(cursor, signature, out var cursorUpdatedAt, out var cursorId))
            return Problem(400, "cursor-invalid", "The cursor does not belong to this requester ticket query.");

        var query = database.Tickets.AsNoTracking().Where(value =>
            value.ProjectId == source.ProjectId &&
            value.RequesterExternalId == source.RequesterExternalId &&
            value.Id != source.Id);
        if (parsedStatus is not null) query = query.Where(value => value.Status == parsedStatus);
        if (cursorUpdatedAt is not null)
            query = query.Where(value => value.UpdatedAt < cursorUpdatedAt || value.UpdatedAt == cursorUpdatedAt && value.Id.CompareTo(cursorId) > 0);

        var tickets = await query.OrderByDescending(value => value.UpdatedAt).ThenBy(value => value.Id)
            .Take(limit + 1)
            .ToListAsync(context.RequestAborted);
        var hasMore = tickets.Count > limit;
        if (hasMore) tickets.RemoveAt(tickets.Count - 1);
        var project = await database.Projects.AsNoTracking().SingleAsync(value => value.Id == source.ProjectId, context.RequestAborted);
        var assigneeIds = tickets.Where(value => value.AssigneeUserId is not null)
            .Select(value => value.AssigneeUserId!.Value).Distinct().ToArray();
        var users = await database.Users.AsNoTracking().Where(value => assigneeIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, context.RequestAborted);
        return Results.Ok(new
        {
            items = tickets.Select(value => TicketSummary(value, project,
                value.AssigneeUserId is { } id ? users.GetValueOrDefault(id) : null)),
            next_cursor = hasMore ? EncodeCursor(tickets[^1], signature) : null,
        });
    }

    private static async Task<IResult> AcquireNextTicket(
        NextTicketRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var actor = context.Actor()!;
        var requestHash = RequestHash(context, JsonSerializer.Serialize(request, JsonOptions(context)));
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var visibleProjectIds = await context.VisibleProjects(database).Select(value => value.Id)
            .ToArrayAsync(context.RequestAborted);
        if (request.ProjectId is { } requestedProjectId && !visibleProjectIds.Contains(requestedProjectId))
            return ProjectNotFound();
        var candidateProjectIds = request.ProjectId is { } projectId ? [projectId] : visibleProjectIds;
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (candidateProjectIds.Length == 0) return NoTicket();

        await using var transaction = await database.Database.BeginTransactionAsync(context.RequestAborted);
        var userId = actor.User.Id;
        var candidateId = await database.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value"
            FROM tickets
            WHERE "ProjectId" = ANY ({candidateProjectIds})
              AND "Status" = 'Open'
              AND ("AssigneeUserId" IS NULL OR "AssigneeUserId" = {userId})
            ORDER BY CASE WHEN "Priority" = 'Urgent' THEN 0 ELSE 1 END,
                     "WaitingSince",
                     "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).FirstOrDefaultAsync(context.RequestAborted);
        if (candidateId == Guid.Empty)
        {
            await transaction.RollbackAsync(context.RequestAborted);
            return NoTicket();
        }

        var ticket = await database.Tickets.Include(value => value.Conversation)
            .AsSplitQuery()
            .SingleAsync(value => value.Id == candidateId, context.RequestAborted);
        var existingEntryCount = ticket.Conversation.Count;
        ticket.Update(
            TicketStatus.InProgress,
            null,
            true,
            actor.User.Id,
            actor.User.Id,
            actor.Agent?.Id,
            DateTimeOffset.UtcNow);
        TrackNewEntries(database, ticket, existingEntryCount);
        var detail = await TicketDetail(ticket, database, context.RequestAborted);
        var body = JsonSerializer.Serialize(detail, JsonOptions(context));
        AddIdempotency(database, actor, key!, requestHash, body, ticket.Version);
        var saveError = await SaveTicket(database, ticket.Id, context.RequestAborted);
        if (saveError is not null)
        {
            await transaction.RollbackAsync(context.RequestAborted);
            return saveError;
        }
        await transaction.CommitAsync(context.RequestAborted);
        return StoredJson(context, 200, body, ETag(ticket.Version));
    }

    private static async Task<IResult> AddReply(
        string number,
        AddReplyRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return Problem(400, "validation", "A reply is required.");
        if (!TryStatus(request.Status, out var status) || status is null or TicketStatus.Open)
            return Problem(400, "validation", "A reply status must be in_progress, waiting_for_customer, or resolved.");
        var actor = context.Actor()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var requestHash = RequestHash(context, JsonSerializer.Serialize(request, JsonOptions(context)));
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;
        var existingEntryCount = ticket.Conversation.Count;
        ticket.AddPublicReply(Guid.NewGuid(), actor.User.Id, actor.Agent?.Id, request.Message, status.Value, DateTimeOffset.UtcNow);
        TrackNewEntries(database, ticket, existingEntryCount);
        var detail = await TicketDetail(ticket, database, context.RequestAborted);
        var body = JsonSerializer.Serialize(detail, JsonOptions(context));
        AddIdempotency(database, actor, key!, requestHash, body, ticket.Version);
        var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context);
        if (saveError is not null) return saveError;
        return StoredJson(context, 200, body, ETag(ticket.Version));
    }

    private static async Task<IResult> AddNote(
        string number,
        AddNoteRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return Problem(400, "validation", "A note is required.");
        var actor = context.Actor()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var requestHash = RequestHash(context, JsonSerializer.Serialize(request, JsonOptions(context)));
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;
        var existingEntryCount = ticket.Conversation.Count;
        ticket.AddInternalNote(Guid.NewGuid(), actor.User.Id, actor.Agent?.Id, request.Message, DateTimeOffset.UtcNow);
        TrackNewEntries(database, ticket, existingEntryCount);
        var detail = await TicketDetail(ticket, database, context.RequestAborted);
        var body = JsonSerializer.Serialize(detail, JsonOptions(context));
        AddIdempotency(database, actor, key!, requestHash, body, ticket.Version);
        var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context);
        if (saveError is not null) return saveError;
        return StoredJson(context, 200, body, ETag(ticket.Version));
    }

    private static async Task<IResult> UpdateTicket(
        string number,
        UpdateTicketRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!TryStatus(request.Status, out var status)) return Problem(400, "validation", "The ticket status is invalid.");
        if (!TryPriority(request.Priority, out var priority)) return Problem(400, "validation", "The ticket priority is invalid.");
        if (request.ClearAssignee && request.AssigneeId is not null)
            return Problem(400, "validation", "Choose an assignee or clear the assignment, not both.");
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;
        if (request.AssigneeId is { } assigneeId && !await IsEligibleAssignee(database, assigneeId, ticket.ProjectId, context.RequestAborted))
            return Problem(400, "validation", "The assignee must be an active user with access to the ticket's project.");

        var actor = context.Actor()!;
        var changedAt = DateTimeOffset.UtcNow;
        var existingEntryCount = ticket.Conversation.Count;
        ticket.Update(
            status,
            priority,
            request.AssigneeId is not null || request.ClearAssignee,
            request.ClearAssignee ? null : request.AssigneeId,
            actor.User.Id,
            actor.Agent?.Id,
            changedAt);
        TrackNewEntries(database, ticket, existingEntryCount);
        var saveError = await SaveTicket(database, ticket.Id, context.RequestAborted);
        if (saveError is not null) return saveError;
        context.Response.Headers.ETag = ETag(ticket.Version);
        return Results.Ok(await TicketDetail(ticket, database, context.RequestAborted));
    }

    private static async Task<IResult> GetSupportInstructions(
        Guid projectId,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var project = await context.VisibleProjects(database).SingleOrDefaultAsync(value => value.Id == projectId, context.RequestAborted);
        return project is null ? ProjectNotFound() : Results.Ok(InstructionsShape(project));
    }

    private static async Task<IResult> UpdateSupportInstructions(
        Guid projectId,
        UpdateSupportInstructionsRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!context.IsAdministrator()) return Problem(403, "forbidden", "Administrator access is required.");
        if (request.Markdown is null) return Problem(400, "validation", "Markdown content is required.");
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        if (!await context.VisibleProjects(database).AnyAsync(value => value.Id == projectId, context.RequestAborted))
            return ProjectNotFound();
        var project = await database.Projects.SingleAsync(value => value.Id == projectId, context.RequestAborted);
        project.SupportInstructions = request.Markdown;
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.Ok(InstructionsShape(project));
    }

    private static async Task<Ticket?> LoadVisibleTicket(string number, HttpContext context, HelpaffeDbContext database)
    {
        var visibleProjectIds = await context.VisibleProjects(database).Select(value => value.Id)
            .ToArrayAsync(context.RequestAborted);
        return await database.Tickets.Where(value => visibleProjectIds.Contains(value.ProjectId))
            .Include(value => value.Conversation)
            .AsSplitQuery()
            .SingleOrDefaultAsync(value => value.Number == number.Trim().ToUpperInvariant(), context.RequestAborted);
    }

    private static async Task<bool> IsEligibleAssignee(
        HelpaffeDbContext database,
        Guid userId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(value => value.Id == userId && value.IsActive, cancellationToken);
        return user is not null && (user.Role is UserRole.Administrator || await database.UserProjectAccess
            .AnyAsync(value => value.UserId == userId && value.ProjectId == projectId, cancellationToken));
    }

    private static void TrackNewEntries(HelpaffeDbContext database, Ticket ticket, int existingEntryCount) =>
        database.ConversationEntries.AddRange(ticket.Conversation.OrderBy(value => value.Sequence).Skip(existingEntryCount));

    private static bool ExpectedVersion(HttpContext context, int currentVersion, out IResult? error)
    {
        var value = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = Problem(428, "version-required", "If-Match with the last-read ticket version is required.");
            return false;
        }
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' ||
            !int.TryParse(value[1..^1], out var expected) || expected < 1)
        {
            error = Problem(400, "validation", "If-Match must contain one quoted positive ticket version.");
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

    private static async Task<IResult?> SaveTicket(HelpaffeDbContext database, Guid ticketId, CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            var currentVersion = await database.Tickets.AsNoTracking()
                .Where(value => value.Id == ticketId)
                .Select(value => value.Version)
                .SingleAsync(cancellationToken);
            return Stale(currentVersion);
        }
    }

    private static bool IdempotencyKey(HttpContext context, out string? key, out IResult? error)
    {
        key = context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is < 1 or > 200)
        {
            error = Problem(400, "validation", "Idempotency-Key is required and may contain at most 200 characters.");
            return false;
        }
        error = null;
        return true;
    }

    private static string RequestHash(HttpContext context, string body)
    {
        var source = $"{context.Request.Method}\n{context.Request.Path}\n{context.Request.QueryString}\n{body}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static JsonSerializerOptions JsonOptions(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

    private static (string Kind, Guid Id) CredentialKey(BackofficeActor actor) =>
        actor.Agent is null ? ("human", actor.User.Id) : ("agent", actor.Agent.Id);

    private static async Task<IResult?> ExistingIdempotency(
        HelpaffeDbContext database,
        BackofficeActor actor,
        string key,
        string requestHash,
        HttpContext context)
    {
        var credential = CredentialKey(actor);
        var record = await database.IdempotencyRecords.SingleOrDefaultAsync(
            value => value.CredentialKind == credential.Kind && value.CredentialId == credential.Id && value.Key == key,
            context.RequestAborted);
        if (record is null) return null;
        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            database.IdempotencyRecords.Remove(record);
            await database.SaveChangesAsync(context.RequestAborted);
            return null;
        }
        return record.RequestHash == requestHash
            ? StoredJson(context, record.ResponseStatus, record.ResponseBody, record.ResponseETag)
            : Problem(409, "idempotency-mismatch", "The idempotency key was already used for another request.");
    }

    private static void AddIdempotency(
        HelpaffeDbContext database,
        BackofficeActor actor,
        string key,
        string requestHash,
        string responseBody,
        int version)
    {
        var credential = CredentialKey(actor);
        var now = DateTimeOffset.UtcNow;
        database.IdempotencyRecords.Add(new IdempotencyRecord
        {
            CredentialKind = credential.Kind,
            CredentialId = credential.Id,
            Key = key,
            RequestHash = requestHash,
            ResponseStatus = 200,
            ResponseBody = responseBody,
            ResponseETag = ETag(version),
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
        });
    }

    private static async Task<IResult?> SaveIdempotent(
        HelpaffeDbContext database,
        Guid ticketId,
        BackofficeActor actor,
        string key,
        string requestHash,
        HttpContext context)
    {
        try
        {
            await database.SaveChangesAsync(context.RequestAborted);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            var currentVersion = await database.Tickets.AsNoTracking()
                .Where(value => value.Id == ticketId)
                .Select(value => value.Version)
                .SingleAsync(context.RequestAborted);
            return Stale(currentVersion);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var replay = await ExistingIdempotency(database, actor, key, requestHash, context);
            if (replay is not null) return replay;
            throw;
        }
    }

    private static IResult StoredJson(HttpContext context, int status, string body, string etag)
    {
        context.Response.Headers.ETag = etag;
        return Results.Content(body, "application/json", Encoding.UTF8, status);
    }

    private static async Task<object> TicketDetail(Ticket ticket, HelpaffeDbContext database, CancellationToken cancellationToken)
    {
        var project = await database.Projects.AsNoTracking().SingleAsync(value => value.Id == ticket.ProjectId, cancellationToken);
        var userIds = ticket.Conversation.Where(value => value.ActorUserId is not null).Select(value => value.ActorUserId!.Value)
            .Append(ticket.AssigneeUserId ?? Guid.Empty).Where(value => value != Guid.Empty).Distinct().ToArray();
        var users = await database.Users.AsNoTracking().Where(value => userIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        var agentIds = ticket.Conversation.Where(value => value.ActingAgentCredentialId is not null)
            .Select(value => value.ActingAgentCredentialId!.Value).Distinct().ToArray();
        var agents = await database.AgentCredentials.AsNoTracking().Where(value => agentIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, cancellationToken);
        return new
        {
            summary = TicketSummary(ticket, project, ticket.AssigneeUserId is { } id ? users.GetValueOrDefault(id) : null),
            requester = new { external_user_id = ticket.RequesterExternalId, name = ticket.RequesterName, email = ticket.RequesterEmail },
            context = ParseContext(ticket.ContextJson),
            support_instructions = project.SupportInstructions,
            conversation = ticket.Conversation.OrderBy(value => value.Sequence).Select(value => new
            {
                value.Id,
                value.Sequence,
                kind = KindName(value.Kind),
                value.Body,
                is_public = value.IsPublic,
                created_at = value.CreatedAt,
                actor = value.ActorUserId is { } userId ? new
                {
                    user_id = userId,
                    user_name = users.GetValueOrDefault(userId)?.Name,
                    agent_id = value.ActingAgentCredentialId,
                    agent_name = value.ActingAgentCredentialId is { } agentId ? agents.GetValueOrDefault(agentId)?.Name : null,
                } : null,
            }),
        };
    }

    private static object TicketSummary(Ticket ticket, ProjectRecord project, UserRecord? assignee) => new
    {
        ticket.Id,
        number = ticket.Number,
        project = new { project.Id, project.Key, project.Name },
        subject = ticket.Subject,
        status = StatusName(ticket.Status),
        priority = PriorityName(ticket.Priority),
        version = ticket.Version,
        assignee = assignee is null ? null : new { assignee.Id, assignee.Name },
        created_at = ticket.CreatedAt,
        updated_at = ticket.UpdatedAt,
        last_customer_reply_at = ticket.LastCustomerReplyAt,
        waiting_since = ticket.WaitingSince,
    };

    private static object InstructionsShape(ProjectRecord project) => new
    {
        project_id = project.Id,
        project_key = project.Key,
        markdown = project.SupportInstructions,
    };

    private static JsonElement? ParseContext(string? contextJson)
    {
        if (contextJson is null) return null;
        using var document = JsonDocument.Parse(contextJson);
        return document.RootElement.Clone();
    }

    private static bool TryStatus(string? value, out TicketStatus? status)
    {
        status = value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "open" => TicketStatus.Open,
            "in_progress" => TicketStatus.InProgress,
            "waiting_for_customer" => TicketStatus.WaitingForCustomer,
            "resolved" => TicketStatus.Resolved,
            _ => (TicketStatus?)(-1),
        };
        return status != (TicketStatus?)(-1);
    }

    private static bool TryPriority(string? value, out TicketPriority? priority)
    {
        priority = value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "normal" => TicketPriority.Normal,
            "urgent" => TicketPriority.Urgent,
            _ => (TicketPriority?)(-1),
        };
        return priority != (TicketPriority?)(-1);
    }

    private static string StatusName(TicketStatus status) => status switch
    {
        TicketStatus.Open => "open",
        TicketStatus.InProgress => "in_progress",
        TicketStatus.WaitingForCustomer => "waiting_for_customer",
        TicketStatus.Resolved => "resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string PriorityName(TicketPriority priority) => priority switch
    {
        TicketPriority.Normal => "normal",
        TicketPriority.Urgent => "urgent",
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };

    private static string KindName(ConversationEntryKind kind) => kind switch
    {
        ConversationEntryKind.CustomerMessage => "customer_message",
        ConversationEntryKind.PublicReply => "public_reply",
        ConversationEntryKind.InternalNote => "internal_note",
        ConversationEntryKind.SystemEvent => "system_event",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string CursorSignature(
        BackofficeActor actor,
        IEnumerable<Guid> projectIds,
        string? status,
        string? priority,
        Guid? projectId,
        Guid? assigneeId,
        bool mine,
        string? search)
    {
        var source = string.Join('|', actor.User.Id, actor.Agent?.Id, string.Join(',', projectIds), status, priority,
            projectId, assigneeId, mine, search?.Trim().ToUpperInvariant());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    }

    private static string RequesterTicketsCursorSignature(BackofficeActor actor, Ticket sourceTicket, string? status)
    {
        var source = string.Join('|', "requester-tickets", actor.User.Id, actor.Agent?.Id, sourceTicket.Id,
            sourceTicket.ProjectId, sourceTicket.RequesterExternalId, status);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    }

    private static string EncodeCursor(Ticket ticket, string signature)
    {
        var source = $"{ticket.UpdatedAt.UtcTicks}:{ticket.Id:N}:{signature}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(source)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
            if (parts.Length != 3 || parts[2] != signature || !long.TryParse(parts[0], out var ticks) || !Guid.TryParseExact(parts[1], "N", out id))
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

    private static IResult NotFound() => Problem(404, "not-found", "The ticket was not found in the caller's project scope.");
    private static IResult ProjectNotFound() => Problem(404, "not-found", "The project was not found in the caller's project scope.");
    private static IResult NoTicket() => Problem(404, "no-ticket", "No eligible open ticket is currently available.");
    private static string ETag(int version) => $"\"{version}\"";
    private static IResult Stale(int currentVersion) => Results.Json(new
    {
        type = "/problems/stale",
        title = "The ticket changed",
        status = 412,
        detail = "Read the current ticket and retry with its version.",
        current_version = currentVersion,
    }, contentType: "application/problem+json", statusCode: 412);
    private static IResult Problem(int status, string code, string detail) => Results.Json(new
    {
        type = $"/problems/{code}", title = code.Replace('-', ' '), status, detail,
    }, contentType: "application/problem+json", statusCode: status);

    private sealed record AddReplyRequest(string Message, string Status);
    private sealed record AddNoteRequest(string Message);
    private sealed record NextTicketRequest(Guid? ProjectId);
    private sealed record UpdateTicketRequest(string? Status, string? Priority, Guid? AssigneeId, bool ClearAssignee = false);
    private sealed record UpdateSupportInstructionsRequest(string Markdown);
}
