using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Notifications;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Helpaffe.Api.Http;

public static class NotificationRoutes
{
    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/api/backoffice/tickets/{number}/notifications/{notificationId:guid}/retry",
            RetryNotification);
        return endpoints;
    }

    private static async Task<IResult> RetryNotification(
        string number,
        Guid notificationId,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is < 1 or > 200)
            return Problem(400, "validation", "Idempotency-Key is required and may contain at most 200 characters.");
        var actor = context.Actor()!;
        var credential = actor.Agent is null ? (Kind: "human", Id: actor.User.Id) : (Kind: "agent", Id: actor.Agent.Id);
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{context.Request.Method}\n{context.Request.Path}\n{context.Request.QueryString}")));
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var existing = await database.IdempotencyRecords.SingleOrDefaultAsync(value =>
            value.CredentialKind == credential.Kind && value.CredentialId == credential.Id && value.Key == key,
            context.RequestAborted);
        if (existing is not null && existing.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return existing.RequestHash == requestHash
                ? Results.Content(existing.ResponseBody, "application/json", Encoding.UTF8, existing.ResponseStatus)
                : Problem(409, "idempotency-mismatch", "The idempotency key was already used for another request.");
        }
        if (existing is not null)
        {
            database.IdempotencyRecords.Remove(existing);
            await database.SaveChangesAsync(context.RequestAborted);
        }

        var visibleProjectIds = await context.VisibleProjects(database).Select(value => value.Id)
            .ToArrayAsync(context.RequestAborted);
        var ticket = await database.Tickets.AsNoTracking().SingleOrDefaultAsync(value =>
            visibleProjectIds.Contains(value.ProjectId) && value.Number == number.Trim().ToUpperInvariant(),
            context.RequestAborted);
        if (ticket is null) return Problem(404, "not-found", "The ticket was not found in the caller's project scope.");
        var delivery = await database.NotificationDeliveries.SingleOrDefaultAsync(value =>
            value.Id == notificationId && value.TicketId == ticket.Id,
            context.RequestAborted);
        if (delivery is null) return Problem(404, "not-found", "The notification was not found for this ticket.");
        if (delivery.Status != NotificationOutbox.Failed)
            return Problem(409, "notification-not-failed", "Only a failed notification can be retried.");

        delivery.Status = NotificationOutbox.Pending;
        delivery.AttemptCount = 0;
        delivery.NextAttemptAt = DateTimeOffset.UtcNow;
        delivery.SubmittedAt = null;
        delivery.LastError = null;
        var body = JsonSerializer.Serialize(TicketRoutes.NotificationShape(delivery), JsonOptions(context));
        var now = DateTimeOffset.UtcNow;
        database.IdempotencyRecords.Add(new IdempotencyRecord
        {
            CredentialKind = credential.Kind,
            CredentialId = credential.Id,
            Key = key,
            RequestHash = requestHash,
            ResponseStatus = 200,
            ResponseBody = body,
            ResponseETag = string.Empty,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
        });
        try
        {
            await database.SaveChangesAsync(context.RequestAborted);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var replay = await database.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(value =>
                value.CredentialKind == credential.Kind && value.CredentialId == credential.Id && value.Key == key,
                context.RequestAborted);
            if (replay is not null && replay.RequestHash == requestHash)
                return Results.Content(replay.ResponseBody, "application/json", Encoding.UTF8, replay.ResponseStatus);
            throw;
        }
        return Results.Content(body, "application/json", Encoding.UTF8, 200);
    }

    private static JsonSerializerOptions JsonOptions(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

    private static IResult Problem(int status, string code, string detail) => Results.Json(new
    {
        type = $"/problems/{code}", title = code.Replace('-', ' '), status, detail,
    }, contentType: "application/problem+json", statusCode: status);
}
