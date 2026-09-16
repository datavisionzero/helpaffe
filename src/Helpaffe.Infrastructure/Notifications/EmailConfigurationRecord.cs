namespace Helpaffe.Infrastructure.Notifications;

public sealed class ProjectEmailSettingsRecord
{
    public Guid ProjectId { get; init; }
    public string Language { get; set; } = "en";
    public required string SmtpHost { get; set; }
    public int SmtpPort { get; set; }
    public bool SmtpUseTls { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPasswordCiphertext { get; set; }
    public required string SenderName { get; set; }
    public required string SenderEmail { get; set; }
    public required string SupportRecipientsJson { get; set; }
    public required string BrandName { get; set; }
    public string? BrandLogoUrl { get; set; }
    public required string BrandColor { get; set; }
    public required string CustomerTicketUrlTemplate { get; set; }
    public required string BackofficeTicketUrlTemplate { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectEmailTemplateRecord
{
    public Guid ProjectId { get; init; }
    public required string Type { get; init; }
    public required string Subject { get; set; }
    public required string TextBody { get; set; }
    public required string HtmlBody { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class NotificationDeliveryRecord
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public Guid TicketId { get; init; }
    public required string Type { get; init; }
    public required string TargetKind { get; init; }
    public string? RecipientEmail { get; set; }
    public string? RecipientName { get; set; }
    public required string DataJson { get; init; }
    public required string Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
