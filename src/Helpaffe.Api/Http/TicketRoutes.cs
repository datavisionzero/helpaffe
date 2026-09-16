using System.Security.Cryptography;
using System.Text;
using Helpaffe.Domain.Identity;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Http;

public static class TicketRoutes
{
    public static IEndpointRouteBuilder MapTickets(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/backoffice");
        group.MapGet("/tickets", ListTickets);
        group.MapGet("/tickets/{number}", GetTicket);
        group.MapPatch("/tickets/{number}", UpdateTicket);
        group.MapPost("/tickets/{number}/replies", AddReply);
        group.MapPost("/tickets/{number}/notes", AddNote);
        group.MapGet("/projects/{projectId:guid}/support-instructions", GetSupportInstructions);
        group.MapPut("/projects/{projectId:guid}/support-instructions", UpdateSupportInstructions);
        return endpoints;
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
            var pattern = $"%{search.Trim()}%";
            query = query.Where(value =>
                EF.Functions.ILike(value.Number, pattern) ||
                EF.Functions.ILike(value.Subject, pattern) ||
                EF.Functions.ILike(value.RequesterName, pattern) ||
                EF.Functions.ILike(value.RequesterEmail, pattern));
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
        return ticket is null ? NotFound() : Results.Ok(await TicketDetail(ticket, database, context.RequestAborted));
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
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        var actor = context.Actor()!;
        var existingEntryCount = ticket.Conversation.Count;
        ticket.AddPublicReply(Guid.NewGuid(), actor.User.Id, actor.Agent?.Id, request.Message, status.Value, DateTimeOffset.UtcNow);
        TrackNewEntries(database, ticket, existingEntryCount);
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.Ok(await TicketDetail(ticket, database, context.RequestAborted));
    }

    private static async Task<IResult> AddNote(
        string number,
        AddNoteRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return Problem(400, "validation", "A note is required.");
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        var actor = context.Actor()!;
        var existingEntryCount = ticket.Conversation.Count;
        ticket.AddInternalNote(Guid.NewGuid(), actor.User.Id, actor.Agent?.Id, request.Message, DateTimeOffset.UtcNow);
        TrackNewEntries(database, ticket, existingEntryCount);
        await database.SaveChangesAsync(context.RequestAborted);
        return Results.Ok(await TicketDetail(ticket, database, context.RequestAborted));
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
        if (request.AssigneeId is { } assigneeId && !await IsEligibleAssignee(database, assigneeId, ticket.ProjectId, context.RequestAborted))
            return Problem(400, "validation", "The assignee must be an active user with access to the ticket's project.");

        var actor = context.Actor()!;
        var changedAt = DateTimeOffset.UtcNow;
        var existingEntryCount = ticket.Conversation.Count;
        if (status is not null) ticket.ChangeStatus(status.Value, actor.User.Id, actor.Agent?.Id, changedAt);
        if (priority is not null) ticket.ChangePriority(priority.Value, actor.User.Id, actor.Agent?.Id, changedAt);
        if (request.AssigneeId is not null || request.ClearAssignee)
            ticket.Assign(request.ClearAssignee ? null : request.AssigneeId, actor.User.Id, actor.Agent?.Id, changedAt);
        TrackNewEntries(database, ticket, existingEntryCount);
        await database.SaveChangesAsync(context.RequestAborted);
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
        assignee = assignee is null ? null : new { assignee.Id, assignee.Name },
        created_at = ticket.CreatedAt,
        updated_at = ticket.UpdatedAt,
        last_customer_reply_at = ticket.LastCustomerReplyAt,
    };

    private static object InstructionsShape(ProjectRecord project) => new
    {
        project_id = project.Id,
        project_key = project.Key,
        markdown = project.SupportInstructions,
    };

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
    private static IResult Problem(int status, string code, string detail) => Results.Json(new
    {
        type = $"/problems/{code}", title = code.Replace('-', ' '), status, detail,
    }, contentType: "application/problem+json", statusCode: status);

    private sealed record AddReplyRequest(string Message, string Status);
    private sealed record AddNoteRequest(string Message);
    private sealed record UpdateTicketRequest(string? Status, string? Priority, Guid? AssigneeId, bool ClearAssignee = false);
    private sealed record UpdateSupportInstructionsRequest(string Markdown);
}
