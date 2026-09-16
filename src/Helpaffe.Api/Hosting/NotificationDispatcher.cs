using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Helpaffe.Infrastructure.Notifications;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Hosting;

public sealed class NotificationDispatcher(
    IDbContextFactory<HelpaffeDbContext> factory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<NotificationDispatcher> logger)
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
    ];

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var id = await database.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value"
            FROM notification_deliveries
            WHERE "Status" = {NotificationOutbox.Pending}
              AND "NextAttemptAt" <= {now}
            ORDER BY "NextAttemptAt", "CreatedAt", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).FirstOrDefaultAsync(cancellationToken);
        if (id == Guid.Empty)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var delivery = await database.NotificationDeliveries.SingleAsync(value => value.Id == id, cancellationToken);
        try
        {
            var settings = await database.ProjectEmailSettings.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ProjectId == delivery.ProjectId, cancellationToken)
                ?? throw new PermanentNotificationException("Project email settings are not configured.");
            if (delivery.TargetKind == NotificationOutbox.SupportRecipients && delivery.RecipientEmail is null)
                ExpandSupportRecipients(database, delivery, settings, now);
            if (string.IsNullOrWhiteSpace(delivery.RecipientEmail))
                throw new PermanentNotificationException("No notification recipient is configured.");
            var project = await database.Projects.AsNoTracking().SingleAsync(value => value.Id == delivery.ProjectId, cancellationToken);
            var template = await database.ProjectEmailTemplates.AsNoTracking().SingleOrDefaultAsync(
                value => value.ProjectId == delivery.ProjectId && value.Type == delivery.Type,
                cancellationToken);
            var rendered = NotificationEmailRenderer.Render(project, settings, template, delivery);
            await SmtpDelivery.SendAsync(
                settings,
                SmtpSecretProtector.FromConfiguration(configuration),
                delivery.RecipientEmail,
                rendered,
                cancellationToken);
            delivery.AttemptCount++;
            delivery.Status = NotificationOutbox.Submitted;
            delivery.NextAttemptAt = null;
            delivery.SubmittedAt = now;
            delivery.LastError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplyFailure(delivery, exception, now);
            logger.LogWarning(
                "Notification {NotificationId} for ticket {TicketId} failed on attempt {AttemptCount}: {Failure}",
                delivery.Id,
                delivery.TicketId,
                delivery.AttemptCount,
                delivery.LastError);
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static void ExpandSupportRecipients(
        HelpaffeDbContext database,
        NotificationDeliveryRecord delivery,
        ProjectEmailSettingsRecord settings,
        DateTimeOffset now)
    {
        var recipients = (JsonSerializer.Deserialize<string[]>(settings.SupportRecipientsJson) ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (recipients.Length == 0)
            throw new PermanentNotificationException("No project support recipients are configured.");
        delivery.RecipientEmail = recipients[0];
        foreach (var recipient in recipients.Skip(1))
        {
            database.NotificationDeliveries.Add(new NotificationDeliveryRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = delivery.ProjectId,
                TicketId = delivery.TicketId,
                Type = delivery.Type,
                TargetKind = delivery.TargetKind,
                RecipientEmail = recipient,
                DataJson = delivery.DataJson,
                Status = NotificationOutbox.Pending,
                AttemptCount = 0,
                NextAttemptAt = now,
                CreatedAt = delivery.CreatedAt,
            });
        }
    }

    private static void ApplyFailure(
        NotificationDeliveryRecord delivery,
        Exception exception,
        DateTimeOffset now)
    {
        delivery.AttemptCount++;
        delivery.SubmittedAt = null;
        delivery.LastError = FailureMessage(exception);
        var transient = IsTransient(exception);
        if (transient && delivery.AttemptCount <= RetryDelays.Length)
        {
            delivery.Status = NotificationOutbox.Pending;
            delivery.NextAttemptAt = now.Add(RetryDelays[delivery.AttemptCount - 1]);
        }
        else
        {
            delivery.Status = NotificationOutbox.Failed;
            delivery.NextAttemptAt = null;
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        PermanentNotificationException => false,
        CryptographicException => false,
        FormatException => false,
        InvalidOperationException => false,
        SmtpFailedRecipientException recipient => recipient.StatusCode is
            SmtpStatusCode.MailboxBusy or
            SmtpStatusCode.LocalErrorInProcessing or
            SmtpStatusCode.InsufficientStorage or
            SmtpStatusCode.TransactionFailed,
        SmtpException smtp => smtp.StatusCode is
            SmtpStatusCode.GeneralFailure or
            SmtpStatusCode.ServiceNotAvailable or
            SmtpStatusCode.MailboxBusy or
            SmtpStatusCode.LocalErrorInProcessing or
            SmtpStatusCode.InsufficientStorage or
            SmtpStatusCode.TransactionFailed,
        _ => true,
    };

    private static string FailureMessage(Exception exception) => exception switch
    {
        PermanentNotificationException permanent => permanent.Message,
        CryptographicException => "The SMTP password could not be decrypted.",
        InvalidOperationException invalid => invalid.Message,
        SmtpFailedRecipientException => "The SMTP server rejected the recipient.",
        SmtpException => "SMTP delivery failed.",
        _ => "Temporary email delivery failure.",
    };

    private sealed class PermanentNotificationException(string message) : Exception(message);
}

public sealed class NotificationWorker(
    NotificationDispatcher dispatcher,
    IConfiguration configuration,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Notifications:WorkerEnabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await dispatcher.ProcessOneAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The notification worker cycle failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
