using Helpaffe.Application.Notifications;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Notifications;

namespace Helpaffe.Api.Hosting;

public static class NotificationEmailRenderer
{
    public static RenderedEmail Render(
        ProjectRecord project,
        ProjectEmailSettingsRecord settings,
        ProjectEmailTemplateRecord? template,
        NotificationDeliveryRecord delivery)
    {
        var definition = EmailTemplateCatalog.Get(delivery.Type);
        var data = System.Text.Json.JsonSerializer.Deserialize<NotificationData>(delivery.DataJson)
            ?? throw new InvalidOperationException("Notification data is invalid.");
        var linkTemplate = definition.CustomerAudience
            ? settings.CustomerTicketUrlTemplate
            : settings.BackofficeTicketUrlTemplate;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["product_name"] = data.ProductName,
            ["brand_name"] = settings.BrandName,
            ["brand_color"] = settings.BrandColor,
            ["brand_logo_url"] = settings.BrandLogoUrl ?? string.Empty,
            ["customer_name"] = data.CustomerName,
            ["customer_email"] = data.CustomerEmail,
            ["ticket_number"] = data.TicketNumber,
            ["ticket_subject"] = data.TicketSubject,
            ["ticket_url"] = linkTemplate.Replace("{{ticket_number}}", Uri.EscapeDataString(data.TicketNumber), StringComparison.Ordinal),
            ["message"] = data.Message,
            ["assignee_name"] = data.AssigneeName,
        };
        return EmailTemplateCatalog.Render(
            template?.Subject ?? definition.Subject,
            template?.TextBody ?? definition.TextBody,
            template?.HtmlBody ?? definition.HtmlBody,
            values);
    }
}
