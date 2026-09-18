using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpaffe.Api.Hosting;
using Helpaffe.Application.Attachments;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Notifications;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Helpaffe.Api.Http;

public static class ProductRoutes
{
    public static IEndpointRouteBuilder MapProduct(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/product");
        group.MapGet("/project", (HttpContext context) =>
        {
            var project = context.ProductActor()!.Project;
            return Results.Ok(new { project.Id, project.Key, project.Name });
        });
        group.MapPost("/tickets", CreateTicket);
        group.MapGet("/tickets", ListTickets);
        group.MapGet("/tickets/{number}", GetTicket);
        group.MapPost("/tickets/{number}/replies", AddReply);
        group.MapGet("/tickets/{number}/attachments/{attachmentId:guid}", DownloadAttachment);
        return endpoints;
    }

    private static async Task<IResult> CreateTicket(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        TicketWorkNotifier workNotifier,
        IAttachmentStorage attachmentStorage)
    {
        var parsed = await AttachmentRequests.ReadAsync<CreateTicketRequest>(
            context,
            JsonOptions(context),
            form => new(
                form["external_user_id"].ToString(),
                form["name"].ToString(),
                form["email"].ToString(),
                form["subject"].ToString(),
                form["message"].ToString(),
                FormContext(form["context"].ToString())),
            "external_user_id", "name", "email", "subject", "message", "context");
        if (parsed.Error is not null) return Problem(parsed.Error.Status, parsed.Error.Code, parsed.Error.Detail);
        var request = parsed.Model!;
        var validation = ValidateRequester(request.ExternalUserId, request.Name, request.Email);
        if (validation is not null) return validation;
        if (!Required(request.Subject, 300)) return Problem(400, "validation", "Subject is required and may contain at most 300 characters.");
        if (string.IsNullOrWhiteSpace(request.Message)) return Problem(400, "validation", "A message is required.");
        var contextJson = ContextJson(request.Context, out var contextError);
        if (contextError is not null) return contextError;
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;

        var actor = context.ProductActor()!;
        var requestHash = RequestHash(context, parsed.CanonicalBody);
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;

        var now = DateTimeOffset.UtcNow;
        var initialEntryId = Guid.NewGuid();
        var ticket = Ticket.Create(
            Guid.NewGuid(),
            NewTicketNumber(),
            actor.Project.Id,
            request.Subject,
            request.ExternalUserId,
            request.Name,
            request.Email,
            request.Message,
            now,
            initialEntryId,
            contextJson: contextJson);
        database.Tickets.Add(ticket);
        var storedKeys = await AttachmentRequests.StoreAsync(
            ticket, initialEntryId, parsed.Files, attachmentStorage, database, now, context.RequestAborted);
        var persisted = false;
        try
        {
            NotificationOutbox.AddNewTicket(database, ticket, actor.Project, now,
                MessageWithAttachments(request.Message, ticket.Attachments.Where(value => value.ConversationEntryId == initialEntryId)));
            var body = JsonSerializer.Serialize(ProductTicketDetail(ticket), JsonOptions(context));
            AddIdempotency(database, actor, key!, requestHash, 201, body, ticket.Version);
            var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context, creating: true);
            if (saveError is not null) return saveError;
            persisted = true;
            workNotifier.Signal();
            context.Response.Headers.Location = $"/api/product/tickets/{ticket.Number}?external_user_id={Uri.EscapeDataString(ticket.RequesterExternalId)}";
            return StoredJson(context, 201, body, ETag(ticket.Version));
        }
        finally
        {
            if (!persisted)
                await AttachmentRequests.DeleteAsync(attachmentStorage, storedKeys, CancellationToken.None);
        }
    }

    private static async Task<IResult> ListTickets(
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        string? external_user_id = null,
        int limit = 50,
        string? cursor = null)
    {
        if (!Required(external_user_id, 200))
            return Problem(400, "validation", "External user id is required and may contain at most 200 characters.");
        if (limit is < 1 or > 100) return Problem(400, "validation", "Limit must be between 1 and 100.");
        var actor = context.ProductActor()!;
        var signature = CursorSignature(actor, external_user_id!);
        if (!TryCursor(cursor, signature, out var cursorUpdatedAt, out var cursorId))
            return Problem(400, "cursor-invalid", "The cursor does not belong to this ticket query.");

        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var query = database.Tickets.AsNoTracking().Where(value =>
            value.ProjectId == actor.Project.Id && value.RequesterExternalId == external_user_id!.Trim());
        if (cursorUpdatedAt is not null)
            query = query.Where(value => value.UpdatedAt < cursorUpdatedAt || value.UpdatedAt == cursorUpdatedAt && value.Id.CompareTo(cursorId) > 0);
        var tickets = await query.OrderByDescending(value => value.UpdatedAt).ThenBy(value => value.Id)
            .Take(limit + 1)
            .ToListAsync(context.RequestAborted);
        var hasMore = tickets.Count > limit;
        if (hasMore) tickets.RemoveAt(tickets.Count - 1);
        return Results.Ok(new
        {
            items = tickets.Select(ProductTicketSummary),
            next_cursor = hasMore ? EncodeCursor(tickets[^1], signature) : null,
        });
    }

    private static async Task<IResult> GetTicket(
        string number,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        string? external_user_id = null)
    {
        if (!Required(external_user_id, 200))
            return Problem(400, "validation", "External user id is required and may contain at most 200 characters.");
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadTicket(number, external_user_id!, context.ProductActor()!, database, context.RequestAborted);
        if (ticket is null) return NotFound();
        context.Response.Headers.ETag = ETag(ticket.Version);
        return Results.Ok(ProductTicketDetail(ticket));
    }

    private static async Task<IResult> AddReply(
        string number,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        TicketWorkNotifier workNotifier,
        IAttachmentStorage attachmentStorage)
    {
        var parsed = await AttachmentRequests.ReadAsync<CustomerReplyRequest>(
            context,
            JsonOptions(context),
            form => new(form["external_user_id"].ToString(), form["message"].ToString()),
            "external_user_id", "message");
        if (parsed.Error is not null) return Problem(parsed.Error.Status, parsed.Error.Code, parsed.Error.Detail);
        var request = parsed.Model!;
        if (!Required(request.ExternalUserId, 200))
            return Problem(400, "validation", "External user id is required and may contain at most 200 characters.");
        if (string.IsNullOrWhiteSpace(request.Message)) return Problem(400, "validation", "A message is required.");
        if (!IdempotencyKey(context, out var key, out var keyError)) return keyError!;
        var actor = context.ProductActor()!;
        var requestHash = RequestHash(context, parsed.CanonicalBody);
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadTicket(number, request.ExternalUserId, actor, database, context.RequestAborted);
        if (ticket is null) return NotFound();
        var replay = await ExistingIdempotency(database, actor, key!, requestHash, context);
        if (replay is not null) return replay;
        if (!ExpectedVersion(context, ticket.Version, out var versionError)) return versionError!;

        var existingEntryCount = ticket.Conversation.Count;
        var now = DateTimeOffset.UtcNow;
        var assignee = ticket.AssigneeUserId is { } assigneeId
            ? await database.Users.AsNoTracking().SingleOrDefaultAsync(value => value.Id == assigneeId, context.RequestAborted)
            : null;
        var entryId = Guid.NewGuid();
        ticket.AddCustomerMessage(entryId, request.Message, now);
        database.ConversationEntries.AddRange(ticket.Conversation.OrderBy(value => value.Sequence).Skip(existingEntryCount));
        var storedKeys = await AttachmentRequests.StoreAsync(
            ticket, entryId, parsed.Files, attachmentStorage, database, now, context.RequestAborted);
        var persisted = false;
        try
        {
            NotificationOutbox.AddCustomerReply(database, ticket, actor.Project,
                MessageWithAttachments(request.Message, ticket.Attachments.Where(value => value.ConversationEntryId == entryId)), assignee, now);
            var body = JsonSerializer.Serialize(ProductTicketDetail(ticket), JsonOptions(context));
            AddIdempotency(database, actor, key!, requestHash, 200, body, ticket.Version);
            var saveError = await SaveIdempotent(database, ticket.Id, actor, key!, requestHash, context, creating: false);
            if (saveError is not null) return saveError;
            persisted = true;
            workNotifier.Signal();
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
        IAttachmentStorage attachmentStorage,
        string? external_user_id = null)
    {
        if (!Required(external_user_id, 200))
            return Problem(400, "validation", "External user id is required and may contain at most 200 characters.");
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var ticket = await LoadTicket(number, external_user_id!, context.ProductActor()!, database, context.RequestAborted);
        var attachment = ticket?.Attachments.SingleOrDefault(value => value.Id == attachmentId && value.IsPublic);
        if (attachment is null) return NotFound();
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

    private static Task<Ticket?> LoadTicket(
        string number,
        string externalUserId,
        ProductActor actor,
        HelpaffeDbContext database,
        CancellationToken cancellationToken) =>
        database.Tickets.Where(value =>
                value.ProjectId == actor.Project.Id &&
                value.RequesterExternalId == externalUserId.Trim() &&
                value.Number == number.Trim().ToUpperInvariant())
            .Include(value => value.Conversation)
            .Include(value => value.Attachments)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);

    private static object ProductTicketDetail(Ticket ticket) => new
    {
        summary = ProductTicketSummary(ticket),
        requester = new
        {
            external_user_id = ticket.RequesterExternalId,
            name = ticket.RequesterName,
            email = ticket.RequesterEmail,
        },
        context = ParseContext(ticket.ContextJson),
        conversation = ticket.Conversation.Where(value => value.IsPublic).OrderBy(value => value.Sequence).Select(value => new
        {
            value.Id,
            value.Sequence,
            kind = value.Kind is ConversationEntryKind.CustomerMessage ? "customer_message" : "public_reply",
            value.Body,
            created_at = value.CreatedAt,
            attachments = ticket.Attachments.Where(attachment =>
                    attachment.ConversationEntryId == value.Id && attachment.IsPublic)
                .OrderBy(attachment => attachment.CreatedAt).ThenBy(attachment => attachment.Id)
                .Select(value => new
                {
                    value.Id,
                    file_name = value.FileName,
                    media_type = value.MediaType,
                    size = value.Size,
                    created_at = value.CreatedAt,
                }),
        }),
    };

    private static JsonElement? FormContext(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string MessageWithAttachments(string message, IEnumerable<TicketAttachment> attachments)
    {
        var names = attachments.Select(value => value.FileName).ToArray();
        return names.Length == 0 ? message : $"{message}\n\nAttachments: {string.Join(", ", names)}";
    }

    private static object ProductTicketSummary(Ticket ticket) => new
    {
        ticket.Id,
        number = ticket.Number,
        subject = ticket.Subject,
        status = ticket.Status switch
        {
            TicketStatus.Open => "open",
            TicketStatus.InProgress => "in_progress",
            TicketStatus.WaitingForCustomer => "waiting_for_customer",
            TicketStatus.Resolved => "resolved",
            _ => throw new ArgumentOutOfRangeException(nameof(ticket)),
        },
        version = ticket.Version,
        created_at = ticket.CreatedAt,
        updated_at = ticket.UpdatedAt,
        last_customer_reply_at = ticket.LastCustomerReplyAt,
    };

    private static JsonElement? ParseContext(string? contextJson)
    {
        if (contextJson is null) return null;
        using var document = JsonDocument.Parse(contextJson);
        return document.RootElement.Clone();
    }

    private static string? ContextJson(JsonElement? contextValue, out IResult? error)
    {
        error = null;
        if (contextValue is null || contextValue.Value.ValueKind is JsonValueKind.Null) return null;
        if (contextValue.Value.ValueKind is not JsonValueKind.Object)
        {
            error = Problem(400, "validation", "Context must be a JSON object.");
            return null;
        }
        var json = contextValue.Value.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024)
        {
            error = Problem(400, "validation", "Context may contain at most 16 KB of UTF-8 JSON.");
            return null;
        }
        return json;
    }

    private static IResult? ValidateRequester(string externalUserId, string name, string email)
    {
        if (!Required(externalUserId, 200)) return Problem(400, "validation", "External user id is required and may contain at most 200 characters.");
        if (!Required(name, 200)) return Problem(400, "validation", "Name is required and may contain at most 200 characters.");
        if (!Required(email, 320)) return Problem(400, "validation", "Email is required and may contain at most 320 characters.");
        return null;
    }

    private static bool Required(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;

    private static string NewTicketNumber() => $"HLP-{Guid.NewGuid():N}"[..24].ToUpperInvariant();

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

    private static async Task<IResult?> ExistingIdempotency(
        HelpaffeDbContext database,
        ProductActor actor,
        string key,
        string requestHash,
        HttpContext context)
    {
        var record = await database.IdempotencyRecords.SingleOrDefaultAsync(value =>
            value.CredentialKind == "product" && value.CredentialId == actor.Credential.Id && value.Key == key,
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
        ProductActor actor,
        string key,
        string requestHash,
        int responseStatus,
        string responseBody,
        int version)
    {
        var now = DateTimeOffset.UtcNow;
        database.IdempotencyRecords.Add(new IdempotencyRecord
        {
            CredentialKind = "product",
            CredentialId = actor.Credential.Id,
            Key = key,
            RequestHash = requestHash,
            ResponseStatus = responseStatus,
            ResponseBody = responseBody,
            ResponseETag = ETag(version),
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
        });
    }

    private static async Task<IResult?> SaveIdempotent(
        HelpaffeDbContext database,
        Guid ticketId,
        ProductActor actor,
        string key,
        string requestHash,
        HttpContext context,
        bool creating)
    {
        try
        {
            await database.SaveChangesAsync(context.RequestAborted);
            return null;
        }
        catch (DbUpdateConcurrencyException) when (!creating)
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

    private static string CursorSignature(ProductActor actor, string externalUserId)
    {
        var source = string.Join('|', "product-tickets", actor.Project.Id, actor.Credential.Id, externalUserId.Trim());
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

    private static IResult NotFound() => Problem(404, "not-found", "The ticket was not found for this end user in the product key's project.");
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

    private sealed record CreateTicketRequest(
        string ExternalUserId,
        string Name,
        string Email,
        string Subject,
        string Message,
        JsonElement? Context);

    private sealed record CustomerReplyRequest(string ExternalUserId, string Message);
}
