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
