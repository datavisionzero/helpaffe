using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpaffe.Domain.Identity;
using Helpaffe.Domain.Tickets;
using Helpaffe.Api.Hosting;
using Helpaffe.Application.Attachments;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Notifications;
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
        group.MapGet("/tickets/wait", WaitForTicketWork);
        group.MapPost("/tickets/next", AcquireNextTicket);
        group.MapGet("/tickets/{number}", GetTicket);
        group.MapGet("/tickets/{number}/requester-tickets", ListRequesterTickets);
        group.MapPatch("/tickets/{number}", UpdateTicket);
        group.MapPut("/tickets/{number}/snooze", SetSnooze);
        group.MapPost("/tickets/{number}/development-references", AddDevelopmentReference);
        group.MapDelete("/tickets/{number}/development-references/{referenceId:guid}", RemoveDevelopmentReference);
        group.MapPost("/tickets/{number}/replies", AddReply);
        group.MapPost("/tickets/{number}/notes", AddNote);
        group.MapGet("/tickets/{number}/attachments/{attachmentId:guid}", DownloadAttachment);
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

        var now = DateTimeOffset.UtcNow;
        var query = database.Tickets.AsNoTracking().Where(value =>
            visibleProjectIds.Contains(value.ProjectId) &&
            (value.SnoozedUntil == null || value.SnoozedUntil <= now));
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

    private static async Task<IResult> WaitForTicketWork(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        TicketWorkNotifier workNotifier,
        TimeProvider timeProvider,
        Guid? project_id = null,
        int timeout_seconds = 20,
        string? cursor = null)
    {
        if (timeout_seconds is < 1 or > 60)
            return Problem(400, "validation", "Timeout seconds must be between 1 and 60.");
        await using (var database = await factory.CreateDbContextAsync(context.RequestAborted))
        {
            if (project_id is { } projectId && !await context.VisibleProjects(database)
                    .AnyAsync(value => value.Id == projectId, context.RequestAborted))
                return ProjectNotFound();
        }

        var signature = WaitCursorSignature(context.Actor()!, project_id);
        var startedAt = timeProvider.GetUtcNow();
        var since = startedAt;
        if (!string.IsNullOrWhiteSpace(cursor) && !TryWaitCursor(cursor, signature, out since))
            return Problem(400, "cursor-invalid", "The cursor does not belong to this ticket wait.");
        var deadline = startedAt.AddSeconds(timeout_seconds);
        var boundary = startedAt;

        while (true)
        {
            var observedVersion = workNotifier.Version;
            boundary = timeProvider.GetUtcNow();
            var work = await FindTicketWork(context, factory, project_id, since, boundary);
            if (work is not null)
            {
                return Results.Ok(new
                {
                    work,
                    cursor = EncodeWaitCursor(boundary, signature),
                    timed_out = false,
                });
            }

            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return Results.Ok(new
                {
                    work = (object?)null,
                    cursor = EncodeWaitCursor(boundary, signature),
                    timed_out = true,
                });
            }

            await workNotifier.WaitForChangeAsync(
                observedVersion,
                remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                context.RequestAborted);
        }
    }

    private static async Task<object?> FindTicketWork(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        Guid? projectId,
        DateTimeOffset since,
        DateTimeOffset boundary)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var visibleProjectIds = await context.VisibleProjects(database).Select(value => value.Id)
            .ToArrayAsync(context.RequestAborted);
        var projectIds = projectId is { } selected && visibleProjectIds.Contains(selected)
            ? [selected]
            : projectId is null ? visibleProjectIds : [];
        if (projectIds.Length == 0) return null;

        var ticket = await database.Tickets.AsNoTracking()
            .Where(value => projectIds.Contains(value.ProjectId) &&
                value.LastCustomerReplyAt > value.CreatedAt &&
                value.LastCustomerReplyAt > since && value.LastCustomerReplyAt <= boundary)
            .OrderBy(value => value.LastCustomerReplyAt)
            .ThenBy(value => value.Id)
            .FirstOrDefaultAsync(context.RequestAborted);
        var kind = "customer_reply";
        if (ticket is null)
        {
            var userId = context.User()!.Id;
            ticket = await database.Tickets.AsNoTracking()
                .Where(value => projectIds.Contains(value.ProjectId) &&
                    value.Status == TicketStatus.Open &&
                    (value.SnoozedUntil == null || value.SnoozedUntil <= boundary) &&
                    (value.AssigneeUserId == null || value.AssigneeUserId == userId))
                .OrderBy(value => value.Priority == TicketPriority.Urgent ? 0 : 1)
                .ThenBy(value => value.WaitingSince)
                .ThenBy(value => value.Id)
                .FirstOrDefaultAsync(context.RequestAborted);
            kind = "open_ticket";
        }
        if (ticket is null) return null;

        var project = await database.Projects.AsNoTracking()
            .SingleAsync(value => value.Id == ticket.ProjectId, context.RequestAborted);
        var assignee = ticket.AssigneeUserId is { } assigneeId
            ? await database.Users.AsNoTracking().SingleOrDefaultAsync(value => value.Id == assigneeId, context.RequestAborted)
            : null;
        return new
        {
            kind,
            ticket = TicketSummary(ticket, project, assignee),
            customer_reply_at = kind == "customer_reply" ? ticket.LastCustomerReplyAt : (DateTimeOffset?)null,
        };
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
              AND (snoozed_until IS NULL OR snoozed_until <= NOW())
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
        var expectedVersion = ticket.Version;
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
        var saveError = await SaveTicket(database, ticket.Id, expectedVersion, context.RequestAborted);
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
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        IAttachmentStorage attachmentStorage)
    {
        var parsed = await AttachmentRequests.ReadAsync<AddReplyRequest>(
            context,
            JsonOptions(context),
            form => new(form["message"].ToString(), form["status"].ToString()),
            "message", "status");
        if (parsed.Error is not null) return Problem(parsed.Error.Status, parsed.Error.Code, parsed.Error.Detail);
        var request = parsed.Model!;
        if (string.IsNullOrWhiteSpace(request.Message)) return Problem(400, "validation", "A reply is required.");
        if (!TryStatus(request.Status, out var status) || status is null or TicketStatus.Open)
            return Problem(400, "validation", "A reply status must be in_progress, waiting_for_customer, or resolved.");
        var actor = context.Actor()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var requestHash = RequestHash(context, parsed.CanonicalBody);
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;
        var existingEntryCount = ticket.Conversation.Count;
        var now = DateTimeOffset.UtcNow;
        var entryId = Guid.NewGuid();
        ticket.AddPublicReply(entryId, actor.User.Id, actor.Agent?.Id, request.Message, status.Value, now);
        TrackNewEntries(database, ticket, existingEntryCount);
        var storedKeys = await AttachmentRequests.StoreAsync(
            ticket, entryId, parsed.Files, attachmentStorage, database, now, context.RequestAborted);
        var persisted = false;
        try
        {
            var replyProject = await database.Projects.AsNoTracking().SingleAsync(value => value.Id == ticket.ProjectId, context.RequestAborted);
            NotificationOutbox.AddSupportReply(database, ticket, replyProject,
                MessageWithAttachments(request.Message, ticket.Attachments.Where(value => value.ConversationEntryId == entryId)), now);
            var detail = await TicketDetail(ticket, database, context.RequestAborted);
            var body = JsonSerializer.Serialize(detail, JsonOptions(context));
            AddIdempotency(database, actor, key!, requestHash, body, ticket.Version);
            var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context);
            if (saveError is not null) return saveError;
            persisted = true;
            return StoredJson(context, 200, body, ETag(ticket.Version));
        }
        finally
        {
            if (!persisted)
                await AttachmentRequests.DeleteAsync(attachmentStorage, storedKeys, CancellationToken.None);
        }
    }

    private static async Task<IResult> AddNote(
        string number,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        IAttachmentStorage attachmentStorage)
    {
        var parsed = await AttachmentRequests.ReadAsync<AddNoteRequest>(
            context,
            JsonOptions(context),
            form => new(form["message"].ToString()),
            "message");
        if (parsed.Error is not null) return Problem(parsed.Error.Status, parsed.Error.Code, parsed.Error.Detail);
        var request = parsed.Model!;
        if (string.IsNullOrWhiteSpace(request.Message)) return Problem(400, "validation", "A note is required.");
        var actor = context.Actor()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var requestHash = RequestHash(context, parsed.CanonicalBody);
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;
        var existingEntryCount = ticket.Conversation.Count;
        var now = DateTimeOffset.UtcNow;
        var entryId = Guid.NewGuid();
        ticket.AddInternalNote(entryId, actor.User.Id, actor.Agent?.Id, request.Message, now);
        TrackNewEntries(database, ticket, existingEntryCount);
        var storedKeys = await AttachmentRequests.StoreAsync(
            ticket, entryId, parsed.Files, attachmentStorage, database, now, context.RequestAborted);
        var persisted = false;
        try
        {
            var detail = await TicketDetail(ticket, database, context.RequestAborted);
            var body = JsonSerializer.Serialize(detail, JsonOptions(context));
            AddIdempotency(database, actor, key!, requestHash, body, ticket.Version);
            var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context);
            if (saveError is not null) return saveError;
            persisted = true;
            return StoredJson(context, 200, body, ETag(ticket.Version));
        }
        finally
        {
            if (!persisted)
                await AttachmentRequests.DeleteAsync(attachmentStorage, storedKeys, CancellationToken.None);
        }
    }

    private static async Task<IResult> DownloadAttachment(
        string number,
        Guid attachmentId,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        IAttachmentStorage attachmentStorage)
    {
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        var attachment = ticket?.Attachments.SingleOrDefault(value => value.Id == attachmentId);
        if (attachment is null) return Problem(404, "not-found", "The attachment was not found in the caller's project scope.");
        try
        {
            var content = await attachmentStorage.OpenReadAsync(attachment.StorageKey, context.RequestAborted);
            return Results.File(content, attachment.MediaType, attachment.FileName, enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return Problem(404, "not-found", "The attachment content is unavailable.");
        }
    }

    private static async Task<IResult> UpdateTicket(
        string number,
        UpdateTicketRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        TicketWorkNotifier workNotifier)
    {
        if (!TryStatus(request.Status, out var status)) return Problem(400, "validation", "The ticket status is invalid.");
        if (!TryPriority(request.Priority, out var priority)) return Problem(400, "validation", "The ticket priority is invalid.");
        if (request.ClearAssignee && request.AssigneeId is not null)
            return Problem(400, "validation", "Choose an assignee or clear the assignment, not both.");
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;
        var expectedVersion = ticket.Version;
        if (request.AssigneeId is { } assigneeId && !await IsEligibleAssignee(database, assigneeId, ticket.ProjectId, context.RequestAborted))
            return Problem(400, "validation", "The assignee must be an active user with access to the ticket's project.");

        var actor = context.Actor()!;
        var previousAssigneeId = ticket.AssigneeUserId;
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
        if (request.AssigneeId is { } newAssigneeId && newAssigneeId != previousAssigneeId && newAssigneeId != actor.User.Id)
        {
            var assignedUser = await database.Users.AsNoTracking().SingleAsync(value => value.Id == newAssigneeId, context.RequestAborted);
            var assignmentProject = await database.Projects.AsNoTracking().SingleAsync(value => value.Id == ticket.ProjectId, context.RequestAborted);
            NotificationOutbox.AddAssignment(database, ticket, assignmentProject, assignedUser, changedAt);
        }
        var saveError = await SaveTicket(database, ticket.Id, expectedVersion, context.RequestAborted);
        if (saveError is not null) return saveError;
        workNotifier.Signal();
        context.Response.Headers.ETag = ETag(ticket.Version);
        return Results.Ok(await TicketDetail(ticket, database, context.RequestAborted));
    }

    private static async Task<IResult> SetSnooze(
        string number,
        SetSnoozeRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        TicketWorkNotifier workNotifier)
    {
        var actor = context.Actor()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var requestHash = RequestHash(context, JsonSerializer.Serialize(request, JsonOptions(context)));
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;

        var changedAt = DateTimeOffset.UtcNow;
        if (request.SnoozedUntil is not null && request.SnoozedUntil <= changedAt)
            return Problem(400, "validation", "Snoozed until must be a future date-time.");
        var existingEntryCount = ticket.Conversation.Count;
        ticket.SetSnooze(request.SnoozedUntil, actor.User.Id, actor.Agent?.Id, changedAt);
        TrackNewEntries(database, ticket, existingEntryCount);
        var detail = await TicketDetail(ticket, database, context.RequestAborted);
        var body = JsonSerializer.Serialize(detail, JsonOptions(context));
        AddIdempotency(database, actor, key!, requestHash, body, ticket.Version);
        var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context);
        if (saveError is not null) return saveError;
        workNotifier.Signal();
        return StoredJson(context, 200, body, ETag(ticket.Version));
    }

    private static async Task<IResult> AddDevelopmentReference(
        string number,
        AddDevelopmentReferenceRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        var actor = context.Actor()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var requestHash = RequestHash(context, JsonSerializer.Serialize(request, JsonOptions(context)));
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;
        if (!TryDevelopmentReferenceType(request.Type, out var type))
            return Problem(400, "validation", "Reference type must be planaffe, github, or gitlab.");
        if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Trim().Length > 200)
            return Problem(400, "validation", "Reference label is required and may contain at most 200 characters.");
        if (string.IsNullOrWhiteSpace(request.Url) || request.Url.Trim().Length > 2048 ||
            !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host))
            return Problem(400, "validation", "Reference URL must be an absolute HTTPS URL with at most 2048 characters.");
        if (ticket.DevelopmentReferences.Any(value => string.Equals(value.Url, request.Url.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Problem(409, "reference-exists", "The development reference already exists on this ticket.");

        var existingEntryCount = ticket.Conversation.Count;
        var reference = ticket.AddDevelopmentReference(
            Guid.NewGuid(), type, request.Url, request.Label, actor.User.Id, actor.Agent?.Id, DateTimeOffset.UtcNow);
        database.DevelopmentReferences.Add(reference);
        TrackNewEntries(database, ticket, existingEntryCount);
        var detail = await TicketDetail(ticket, database, context.RequestAborted);
        var body = JsonSerializer.Serialize(detail, JsonOptions(context));
        AddIdempotency(database, actor, key!, requestHash, body, ticket.Version);
        var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context);
        if (saveError is not null) return saveError;
        return StoredJson(context, 200, body, ETag(ticket.Version));
    }

    private static async Task<IResult> RemoveDevelopmentReference(
        string number,
        Guid referenceId,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        var actor = context.Actor()!;
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadVisibleTicket(number, context, database);
        if (ticket is null) return NotFound();
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var requestHash = RequestHash(context, string.Empty);
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;

        var existingEntryCount = ticket.Conversation.Count;
        var removed = ticket.RemoveDevelopmentReference(referenceId, actor.User.Id, actor.Agent?.Id, DateTimeOffset.UtcNow);
        if (removed is null) return Problem(404, "not-found", "The development reference was not found on this ticket.");
        database.DevelopmentReferences.Remove(removed);
        TrackNewEntries(database, ticket, existingEntryCount);
        var detail = await TicketDetail(ticket, database, context.RequestAborted);
        var body = JsonSerializer.Serialize(detail, JsonOptions(context));
        AddIdempotency(database, actor, key!, requestHash, body, ticket.Version);
        var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context);
        if (saveError is not null) return saveError;
        return StoredJson(context, 200, body, ETag(ticket.Version));
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
            .Include(value => value.DevelopmentReferences)
            .Include(value => value.Attachments)
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

    private static async Task<IResult?> SaveTicket(
        HelpaffeDbContext database,
        Guid ticketId,
        int expectedVersion,
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
            var currentVersion = await database.Tickets.AsNoTracking()
                .Where(value => value.Id == ticketId)
                .Select(value => value.Version)
                .SingleAsync(cancellationToken);
            return Stale(currentVersion);
        }
        catch (DbUpdateException) when (database.Database.CurrentTransaction is null)
        {
            database.ChangeTracker.Clear();
            var currentVersion = await database.Tickets.AsNoTracking()
                .Where(value => value.Id == ticketId)
                .Select(value => value.Version)
                .SingleAsync(cancellationToken);
            if (currentVersion != expectedVersion) return Stale(currentVersion);
            throw;
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
        var storedNotifications = await database.NotificationDeliveries.AsNoTracking()
            .Where(value => value.TicketId == ticket.Id)
            .ToListAsync(cancellationToken);
        var storedNotificationIds = storedNotifications.Select(value => value.Id).ToHashSet();
        var notifications = storedNotifications.Concat(database.NotificationDeliveries.Local
            .Where(value => value.TicketId == ticket.Id && !storedNotificationIds.Contains(value.Id)))
            .OrderBy(value => value.CreatedAt)
            .ThenBy(value => value.Id)
            .ToArray();
        return new
        {
            summary = TicketSummary(ticket, project, ticket.AssigneeUserId is { } id ? users.GetValueOrDefault(id) : null),
            requester = new { external_user_id = ticket.RequesterExternalId, name = ticket.RequesterName, email = ticket.RequesterEmail },
            context = ParseContext(ticket.ContextJson),
            support_instructions = project.SupportInstructions,
            development_references = ticket.DevelopmentReferences.OrderBy(value => value.Position).Select(value => new
            {
                value.Id,
                type = DevelopmentReferenceTypeName(value.Type),
                value.Url,
                value.Label,
                value.Position,
                created_at = value.CreatedAt,
            }),
            notifications = notifications.Select(NotificationShape),
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
                attachments = ticket.Attachments.Where(attachment => attachment.ConversationEntryId == value.Id)
                    .OrderBy(attachment => attachment.CreatedAt).ThenBy(attachment => attachment.Id)
                    .Select(AttachmentShape),
            }),
        };
    }

    private static object AttachmentShape(TicketAttachment value) => new
    {
        value.Id,
        file_name = value.FileName,
        media_type = value.MediaType,
        size = value.Size,
        is_public = value.IsPublic,
        created_at = value.CreatedAt,
    };

    private static string MessageWithAttachments(string message, IEnumerable<TicketAttachment> attachments)
    {
        var names = attachments.Select(value => value.FileName).ToArray();
        return names.Length == 0 ? message : $"{message}\n\nAttachments: {string.Join(", ", names)}";
    }

    internal static object NotificationShape(NotificationDeliveryRecord value) => new
    {
        value.Id,
        type = value.Type,
        target_kind = value.TargetKind,
        recipient_email = value.RecipientEmail,
        status = value.Status,
        attempt_count = value.AttemptCount,
        next_attempt_at = value.NextAttemptAt,
        submitted_at = value.SubmittedAt,
        last_error = value.LastError,
        created_at = value.CreatedAt,
    };

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
        snoozed_until = ticket.SnoozedUntil,
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

    private static bool TryDevelopmentReferenceType(string? value, out DevelopmentReferenceType type)
    {
        type = value?.Trim().ToLowerInvariant() switch
        {
            "planaffe" => DevelopmentReferenceType.Planaffe,
            "github" => DevelopmentReferenceType.GitHub,
            "gitlab" => DevelopmentReferenceType.GitLab,
            _ => (DevelopmentReferenceType)(-1),
        };
        return type != (DevelopmentReferenceType)(-1);
    }

    private static string DevelopmentReferenceTypeName(DevelopmentReferenceType type) => type switch
    {
        DevelopmentReferenceType.Planaffe => "planaffe",
        DevelopmentReferenceType.GitHub => "github",
        DevelopmentReferenceType.GitLab => "gitlab",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
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

    private static string WaitCursorSignature(BackofficeActor actor, Guid? projectId)
    {
        var source = string.Join('|', "ticket-wait", actor.User.Id, actor.Agent?.Id, projectId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    }

    private static string EncodeWaitCursor(DateTimeOffset timestamp, string signature)
    {
        var source = $"{timestamp.UtcTicks}:{signature}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(source)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryWaitCursor(string cursor, string signature, out DateTimeOffset timestamp)
    {
        timestamp = default;
        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split(':');
            if (parts.Length != 2 || parts[1] != signature || !long.TryParse(parts[0], out var ticks)) return false;
            timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
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
    private sealed record SetSnoozeRequest(DateTimeOffset? SnoozedUntil);
    private sealed record AddDevelopmentReferenceRequest(string Type, string Url, string Label);
    private sealed record UpdateSupportInstructionsRequest(string Markdown);
}
