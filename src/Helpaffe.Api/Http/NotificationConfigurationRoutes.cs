using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpaffe.Api.Hosting;
using Helpaffe.Application.Notifications;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Notifications;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Http;

public static partial class NotificationConfigurationRoutes
{
    public static IEndpointRouteBuilder MapNotificationConfiguration(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/backoffice/projects/{projectId:guid}");
        group.MapGet("/email-settings", GetSettings);
        group.MapPut("/email-settings", PutSettings);
        group.MapGet("/email-templates", ListTemplates);
        group.MapGet("/email-templates/{type}", GetTemplate);
        group.MapPut("/email-templates/{type}", PutTemplate);
        group.MapPost("/email-templates/{type}/preview", PreviewTemplate);
        group.MapPost("/email/test", SendTestEmail);
        return endpoints;
    }

    private static async Task<IResult> GetSettings(
        Guid projectId,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var access = await AdministratorProject(projectId, context, factory);
        if (access.Error is not null) return access.Error;
        return Results.Ok(SettingsShape(access.Project!, access.Settings));
    }

    private static async Task<IResult> PutSettings(
        Guid projectId,
        PutEmailSettingsRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        IConfiguration configuration)
    {
        if (request.Smtp is null || request.Sender is null || request.SupportRecipients is null ||
            request.Branding is null || request.TicketLinks is null)
            return Problem(400, "validation", "SMTP, sender, support recipients, branding, and ticket links are required.");
        var validation = ValidateSettings(request);
        if (validation is not null) return Problem(400, "validation", validation);
        await using var access = await AdministratorProject(projectId, context, factory, tracking: true);
        if (access.Error is not null) return access.Error;
        var settings = access.Settings;
        if (!string.IsNullOrEmpty(request.Smtp.Password))
        {
            var protector = SmtpSecretProtector.FromConfiguration(configuration);
            if (protector is null)
                return Problem(503, "secret-key-unavailable", "Secrets:EncryptionKey must be configured as a Base64-encoded 32-byte key before storing SMTP credentials.");
            settings ??= NewSettings(projectId, access.Project!);
            settings.SmtpPasswordCiphertext = protector.Protect(request.Smtp.Password);
        }
        if (!string.IsNullOrWhiteSpace(request.Smtp.Username) && settings?.SmtpPasswordCiphertext is null)
            return Problem(400, "validation", "An SMTP password is required when configuring an SMTP username for the first time.");

        settings ??= NewSettings(projectId, access.Project!);
        settings.Language = "en";
        settings.SmtpHost = request.Smtp.Host.Trim();
        settings.SmtpPort = request.Smtp.Port;
        settings.SmtpUseTls = request.Smtp.UseTls;
        settings.SmtpUsername = EmptyToNull(request.Smtp.Username);
        settings.SenderName = request.Sender.Name.Trim();
        settings.SenderEmail = request.Sender.Email.Trim();
        settings.SupportRecipientsJson = JsonSerializer.Serialize(request.SupportRecipients.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        settings.BrandName = request.Branding.Name.Trim();
        settings.BrandLogoUrl = EmptyToNull(request.Branding.LogoUrl);
        settings.BrandColor = request.Branding.Color.ToUpperInvariant();
        settings.CustomerTicketUrlTemplate = request.TicketLinks.Customer.Trim();
        settings.BackofficeTicketUrlTemplate = request.TicketLinks.Backoffice.Trim();
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        if (access.Settings is null) access.Database!.ProjectEmailSettings.Add(settings);
        await access.Database!.SaveChangesAsync(context.RequestAborted);
        return Results.Ok(SettingsShape(access.Project!, settings));
    }

    private static async Task<IResult> ListTemplates(
        Guid projectId,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        await using var access = await AdministratorProject(projectId, context, factory);
        if (access.Error is not null) return access.Error;
        var records = await access.Database!.ProjectEmailTemplates.AsNoTracking()
            .Where(value => value.ProjectId == projectId)
            .ToDictionaryAsync(value => value.Type, context.RequestAborted);
        return Results.Ok(EmailTemplateCatalog.Types.Select(type => TemplateShape(type, records.GetValueOrDefault(type))));
    }

    private static async Task<IResult> GetTemplate(
        Guid projectId,
        string type,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!EmailTemplateCatalog.Types.Contains(type, StringComparer.Ordinal)) return TemplateNotFound();
        await using var access = await AdministratorProject(projectId, context, factory);
        if (access.Error is not null) return access.Error;
        var record = await access.Database!.ProjectEmailTemplates.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProjectId == projectId && value.Type == type, context.RequestAborted);
        return Results.Ok(TemplateShape(type, record));
    }

    private static async Task<IResult> PutTemplate(
        Guid projectId,
        string type,
        PutEmailTemplateRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        var validation = EmailTemplateCatalog.Validate(type, request.Subject, request.TextBody, request.HtmlBody);
        if (validation is not null) return Problem(400, "validation", validation);
        await using var access = await AdministratorProject(projectId, context, factory, tracking: true);
        if (access.Error is not null) return access.Error;
        var record = await access.Database!.ProjectEmailTemplates.SingleOrDefaultAsync(
            value => value.ProjectId == projectId && value.Type == type,
            context.RequestAborted);
        if (record is null)
        {
            record = new ProjectEmailTemplateRecord
            {
                ProjectId = projectId,
                Type = type,
                Subject = request.Subject,
                TextBody = request.TextBody,
                HtmlBody = request.HtmlBody,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            access.Database.ProjectEmailTemplates.Add(record);
        }
        else
        {
            record.Subject = request.Subject;
            record.TextBody = request.TextBody;
            record.HtmlBody = request.HtmlBody;
            record.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await access.Database.SaveChangesAsync(context.RequestAborted);
        return Results.Ok(TemplateShape(type, record));
    }

    private static async Task<IResult> PreviewTemplate(
        Guid projectId,
        string type,
        TemplateSampleRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory)
    {
        if (!EmailTemplateCatalog.Types.Contains(type, StringComparer.Ordinal)) return TemplateNotFound();
        await using var access = await AdministratorProject(projectId, context, factory);
        if (access.Error is not null) return access.Error;
        var record = await access.Database!.ProjectEmailTemplates.AsNoTracking().SingleOrDefaultAsync(
            value => value.ProjectId == projectId && value.Type == type,
            context.RequestAborted);
        var rendered = Render(access.Project!, access.Settings, type, record, request);
        return Results.Ok(rendered);
    }

    private static async Task<IResult> SendTestEmail(
        Guid projectId,
        TestEmailRequest request,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        IConfiguration configuration)
    {
        if (!ValidEmail(request.Recipient)) return Problem(400, "validation", "A valid test recipient email address is required.");
        if (!EmailTemplateCatalog.Types.Contains(request.TemplateType, StringComparer.Ordinal)) return TemplateNotFound();
        await using var access = await AdministratorProject(projectId, context, factory);
        if (access.Error is not null) return access.Error;
        if (access.Settings is null) return Problem(400, "validation", "Email settings must be configured before sending a test email.");
        var record = await access.Database!.ProjectEmailTemplates.AsNoTracking().SingleOrDefaultAsync(
            value => value.ProjectId == projectId && value.Type == request.TemplateType,
            context.RequestAborted);
        var rendered = Render(access.Project!, access.Settings, request.TemplateType, record, request.Sample ?? new());
        try
        {
            await SmtpDelivery.SendAsync(
                access.Settings,
                SmtpSecretProtector.FromConfiguration(configuration),
                request.Recipient,
                rendered,
                context.RequestAborted);
        }
        catch (Exception exception) when (exception is SmtpException or InvalidOperationException or FormatException or CryptographicException or ArgumentException)
        {
            return Problem(502, "smtp-failed", "The SMTP server did not accept the test email.");
        }
        return Results.Ok(new { recipient = request.Recipient.Trim(), template_type = request.TemplateType, status = "submitted_to_smtp" });
    }

    private static RenderedEmail Render(
        ProjectRecord project,
        ProjectEmailSettingsRecord? settings,
        string type,
        ProjectEmailTemplateRecord? record,
        TemplateSampleRequest sample)
    {
        var definition = EmailTemplateCatalog.Get(type);
        var ticketNumber = EmptyToNull(sample.TicketNumber) ?? "HLP-42";
        var linkTemplate = definition.CustomerAudience
            ? settings?.CustomerTicketUrlTemplate
            : settings?.BackofficeTicketUrlTemplate;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["product_name"] = project.Name,
            ["brand_name"] = settings?.BrandName ?? project.Name,
            ["brand_color"] = settings?.BrandColor ?? "#2F6FED",
            ["brand_logo_url"] = settings?.BrandLogoUrl ?? string.Empty,
            ["customer_name"] = EmptyToNull(sample.CustomerName) ?? "Avery Customer",
            ["customer_email"] = EmptyToNull(sample.CustomerEmail) ?? "avery@example.test",
            ["ticket_number"] = ticketNumber,
            ["ticket_subject"] = EmptyToNull(sample.TicketSubject) ?? "Example support request",
            ["ticket_url"] = (linkTemplate ?? "https://example.invalid/tickets/{{ticket_number}}").Replace("{{ticket_number}}", Uri.EscapeDataString(ticketNumber), StringComparison.Ordinal),
            ["message"] = EmptyToNull(sample.Message) ?? "This is example notification content.",
            ["assignee_name"] = EmptyToNull(sample.AssigneeName) ?? "Support Agent",
        };
        return EmailTemplateCatalog.Render(
            record?.Subject ?? definition.Subject,
            record?.TextBody ?? definition.TextBody,
            record?.HtmlBody ?? definition.HtmlBody,
            values);
    }

    private static object TemplateShape(string type, ProjectEmailTemplateRecord? record)
    {
        var definition = EmailTemplateCatalog.Get(type);
        return new
        {
            type,
            definition.Description,
            subject = record?.Subject ?? definition.Subject,
            text_body = record?.TextBody ?? definition.TextBody,
            html_body = record?.HtmlBody ?? definition.HtmlBody,
            variables = definition.Variables.OrderBy(value => value),
            is_customized = record is not null,
        };
    }

    private static object SettingsShape(ProjectRecord project, ProjectEmailSettingsRecord? settings) => new
    {
        project_id = project.Id,
        project_name = project.Name,
        language = "en",
        smtp = new
        {
            host = settings?.SmtpHost ?? string.Empty,
            port = settings?.SmtpPort ?? 587,
            use_tls = settings?.SmtpUseTls ?? true,
            username = settings?.SmtpUsername,
            password_configured = settings?.SmtpPasswordCiphertext is not null,
        },
        sender = new { name = settings?.SenderName ?? project.Name, email = settings?.SenderEmail ?? string.Empty },
        support_recipients = settings is null ? [] : JsonSerializer.Deserialize<string[]>(settings.SupportRecipientsJson) ?? [],
        branding = new
        {
            name = settings?.BrandName ?? project.Name,
            logo_url = settings?.BrandLogoUrl,
            color = settings?.BrandColor ?? "#2F6FED",
        },
        ticket_links = new
        {
            customer = settings?.CustomerTicketUrlTemplate ?? string.Empty,
            backoffice = settings?.BackofficeTicketUrlTemplate ?? string.Empty,
        },
    };

    private static ProjectEmailSettingsRecord NewSettings(Guid projectId, ProjectRecord project) => new()
    {
        ProjectId = projectId,
        SmtpHost = string.Empty,
        SmtpPort = 587,
        SmtpUseTls = true,
        SenderName = project.Name,
        SenderEmail = string.Empty,
        SupportRecipientsJson = "[]",
        BrandName = project.Name,
        BrandColor = "#2F6FED",
        CustomerTicketUrlTemplate = string.Empty,
        BackofficeTicketUrlTemplate = string.Empty,
    };

    private static string? ValidateSettings(PutEmailSettingsRequest request)
    {
        if (request.Language != "en") return "Language must be en; English is the only supported language.";
        if (string.IsNullOrWhiteSpace(request.Smtp.Host) || request.Smtp.Host.Trim().Length > 255) return "SMTP host is required and may contain at most 255 characters.";
        if (request.Smtp.Port is < 1 or > 65_535) return "SMTP port must be between 1 and 65535.";
        if (request.Smtp.Username?.Length > 320) return "SMTP username may contain at most 320 characters.";
        if (request.Smtp.Password?.Length > 4096) return "SMTP password may contain at most 4096 characters.";
        if (string.IsNullOrWhiteSpace(request.Sender.Name) || request.Sender.Name.Trim().Length > 200 || !ValidEmail(request.Sender.Email))
            return "A sender name and valid sender email address are required.";
        if (request.SupportRecipients.Length < 1 || request.SupportRecipients.Any(value => !ValidEmail(value)))
            return "At least one valid support recipient email address is required.";
        if (string.IsNullOrWhiteSpace(request.Branding.Name) || request.Branding.Name.Trim().Length > 200) return "Brand name is required and may contain at most 200 characters.";
        if (!ColorPattern().IsMatch(request.Branding.Color)) return "Brand color must be a six-digit hexadecimal color such as #2F6FED.";
        if (!string.IsNullOrWhiteSpace(request.Branding.LogoUrl) && !ValidAbsoluteHttpUrl(request.Branding.LogoUrl, allowTicketVariable: false))
            return "Brand logo URL must be an absolute HTTP or HTTPS URL.";
        if (!ValidAbsoluteHttpUrl(request.TicketLinks.Customer, allowTicketVariable: true) ||
            !ValidAbsoluteHttpUrl(request.TicketLinks.Backoffice, allowTicketVariable: true))
            return "Customer and backoffice ticket links must be absolute HTTP or HTTPS URLs containing {{ticket_number}}.";
        return null;
    }

    private static bool ValidAbsoluteHttpUrl(string value, bool allowTicketVariable)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) return false;
        if (allowTicketVariable && !value.Contains("{{ticket_number}}", StringComparison.Ordinal)) return false;
        var candidate = value.Replace("{{ticket_number}}", "HLP-42", StringComparison.Ordinal);
        if (candidate.Contains('{') || candidate.Contains('}')) return false;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }

    private static bool ValidEmail(string? value) => MailAddress.TryCreate(value?.Trim(), out _);
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IResult TemplateNotFound() => Problem(404, "not-found", "The email template type was not found.");
    private static IResult Forbidden() => Problem(403, "forbidden", "Administrator access is required.");
    private static IResult ProjectNotFound() => Problem(404, "not-found", "The project was not found in the caller's project scope.");
    private static IResult Problem(int status, string code, string detail) => Results.Json(new
    {
        type = $"/problems/{code}", title = code.Replace('-', ' '), status, detail,
    }, contentType: "application/problem+json", statusCode: status);

    private static async Task<ProjectAccess> AdministratorProject(
        Guid projectId,
        HttpContext context,
        IDbContextFactory<HelpaffeDbContext> factory,
        bool tracking = false)
    {
        if (!context.IsAdministrator()) return new(null, null, null, Forbidden());
        var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var projectQuery = context.VisibleProjects(database);
        if (!tracking) projectQuery = projectQuery.AsNoTracking();
        var project = await projectQuery.SingleOrDefaultAsync(value => value.Id == projectId, context.RequestAborted);
        if (project is null)
        {
            await database.DisposeAsync();
            return new(null, null, null, ProjectNotFound());
        }
        var settingsQuery = database.ProjectEmailSettings.AsQueryable();
        if (!tracking) settingsQuery = settingsQuery.AsNoTracking();
        var settings = await settingsQuery.SingleOrDefaultAsync(value => value.ProjectId == projectId, context.RequestAborted);
        return new(database, project, settings, null);
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    private sealed record ProjectAccess(
        HelpaffeDbContext? Database,
        ProjectRecord? Project,
        ProjectEmailSettingsRecord? Settings,
        IResult? Error) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Database?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed record PutEmailSettingsRequest(
        string Language,
        SmtpSettingsRequest Smtp,
        SenderRequest Sender,
        string[] SupportRecipients,
        BrandingRequest Branding,
        TicketLinksRequest TicketLinks);
    private sealed record SmtpSettingsRequest(string Host, int Port, bool UseTls, string? Username, string? Password);
    private sealed record SenderRequest(string Name, string Email);
    private sealed record BrandingRequest(string Name, string? LogoUrl, string Color);
    private sealed record TicketLinksRequest(string Customer, string Backoffice);
    private sealed record PutEmailTemplateRequest(string Subject, string TextBody, string HtmlBody);
    private sealed record TestEmailRequest(string Recipient, string TemplateType, TemplateSampleRequest? Sample);
    private sealed record TemplateSampleRequest(
        string? CustomerName = null,
        string? CustomerEmail = null,
        string? TicketNumber = null,
        string? TicketSubject = null,
        string? Message = null,
        string? AssigneeName = null);
}
