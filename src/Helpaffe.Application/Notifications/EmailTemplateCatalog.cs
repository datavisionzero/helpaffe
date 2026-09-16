using System.Net;
using System.Text.RegularExpressions;

namespace Helpaffe.Application.Notifications;

public sealed record EmailTemplateDefinition(
    string Type,
    string Description,
    string Subject,
    string TextBody,
    string HtmlBody,
    IReadOnlySet<string> Variables,
    bool CustomerAudience);

public sealed record RenderedEmail(string Subject, string TextBody, string HtmlBody);

public static partial class EmailTemplateCatalog
{
    public static readonly IReadOnlyList<string> Types =
    [
        "new_ticket_customer",
        "new_ticket_support",
        "customer_reply_support",
        "public_reply_customer",
        "assignment_support",
    ];

    private static readonly IReadOnlySet<string> CommonVariables = new HashSet<string>(StringComparer.Ordinal)
    {
        "product_name", "brand_name", "brand_color", "brand_logo_url", "customer_name", "customer_email",
        "ticket_number", "ticket_subject", "ticket_url", "message", "assignee_name",
    };

    public static EmailTemplateDefinition Get(string type) => type switch
    {
        "new_ticket_customer" => Definition(
            type,
            "Confirmation sent to the customer when a ticket is created.",
            "We received {{ticket_number}}: {{ticket_subject}}",
            "Hello {{customer_name}},\n\nWe received your request for {{product_name}}.\n\n{{message}}\n\nView your ticket: {{ticket_url}}\n\n{{brand_name}}",
            "<h1 style=\"color:{{brand_color}}\">{{brand_name}}</h1><p>Hello {{customer_name}},</p><p>We received your request for {{product_name}}.</p><blockquote>{{message}}</blockquote><p><a href=\"{{ticket_url}}\">View ticket {{ticket_number}}</a></p>",
            customerAudience: true),
        "new_ticket_support" => Definition(
            type,
            "Notification sent to project support recipients for a new ticket.",
            "New ticket {{ticket_number}}: {{ticket_subject}}",
            "{{customer_name}} ({{customer_email}}) opened {{ticket_number}} for {{product_name}}.\n\n{{message}}\n\nOpen in helpaffe: {{ticket_url}}",
            "<h1 style=\"color:{{brand_color}}\">New ticket {{ticket_number}}</h1><p>{{customer_name}} ({{customer_email}}) opened a ticket for {{product_name}}.</p><blockquote>{{message}}</blockquote><p><a href=\"{{ticket_url}}\">Open in helpaffe</a></p>",
            customerAudience: false),
        "customer_reply_support" => Definition(
            type,
            "Notification sent to the assignee or project support recipients for a customer reply.",
            "Customer reply on {{ticket_number}}: {{ticket_subject}}",
            "{{customer_name}} replied to {{ticket_number}}.\n\n{{message}}\n\nOpen in helpaffe: {{ticket_url}}",
            "<h1 style=\"color:{{brand_color}}\">Customer reply on {{ticket_number}}</h1><p>{{customer_name}} replied:</p><blockquote>{{message}}</blockquote><p><a href=\"{{ticket_url}}\">Open in helpaffe</a></p>",
            customerAudience: false),
        "public_reply_customer" => Definition(
            type,
            "Notification sent to the customer for a public support reply.",
            "Reply on {{ticket_number}}: {{ticket_subject}}",
            "Hello {{customer_name}},\n\nSupport replied to your request:\n\n{{message}}\n\nView your ticket: {{ticket_url}}\n\n{{brand_name}}",
            "<h1 style=\"color:{{brand_color}}\">{{brand_name}}</h1><p>Hello {{customer_name}},</p><p>Support replied to your request:</p><blockquote>{{message}}</blockquote><p><a href=\"{{ticket_url}}\">View ticket {{ticket_number}}</a></p>",
            customerAudience: true),
        "assignment_support" => Definition(
            type,
            "Notification sent when a ticket is assigned to another support user.",
            "{{ticket_number}} was assigned to you",
            "Hello {{assignee_name}},\n\n{{ticket_number}} ({{ticket_subject}}) was assigned to you.\n\nOpen in helpaffe: {{ticket_url}}",
            "<h1 style=\"color:{{brand_color}}\">Ticket assigned</h1><p>Hello {{assignee_name}},</p><p>{{ticket_number}} ({{ticket_subject}}) was assigned to you.</p><p><a href=\"{{ticket_url}}\">Open in helpaffe</a></p>",
            customerAudience: false),
        _ => throw new ArgumentOutOfRangeException(nameof(type), "The email template type is unknown."),
    };

    public static string? Validate(string type, string subject, string textBody, string htmlBody)
    {
        if (!Types.Contains(type, StringComparer.Ordinal)) return "The email template type is unknown.";
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 300) return "The subject is required and may contain at most 300 characters.";
        if (string.IsNullOrWhiteSpace(textBody) || string.IsNullOrWhiteSpace(htmlBody)) return "Both text and HTML bodies are required.";
        if (textBody.Length > 65_536 || htmlBody.Length > 65_536) return "Each template body may contain at most 65,536 characters.";
        foreach (var content in new[] { subject, textBody, htmlBody })
        {
            var matches = VariablePattern().Matches(content);
            var withoutKnownSyntax = VariablePattern().Replace(content, string.Empty);
            if (withoutKnownSyntax.Contains("{{", StringComparison.Ordinal) || withoutKnownSyntax.Contains("}}", StringComparison.Ordinal))
                return "Template variables must use the {{variable_name}} syntax.";
            var unknown = matches.Select(match => match.Groups[1].Value)
                .FirstOrDefault(variable => !CommonVariables.Contains(variable));
            if (unknown is not null) return $"Unknown template variable: {unknown}.";
        }
        return null;
    }

    public static RenderedEmail Render(
        string subject,
        string textBody,
        string htmlBody,
        IReadOnlyDictionary<string, string> values)
    {
        string Replace(string input, bool html) => VariablePattern().Replace(input, match =>
        {
            var value = values.GetValueOrDefault(match.Groups[1].Value) ?? string.Empty;
            return html ? WebUtility.HtmlEncode(value) : value;
        });
        return new RenderedEmail(
            Replace(subject, html: false).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal),
            Replace(textBody, html: false),
            Replace(htmlBody, html: true));
    }

    private static EmailTemplateDefinition Definition(
        string type,
        string description,
        string subject,
        string textBody,
        string htmlBody,
        bool customerAudience) =>
        new(type, description, subject, textBody, htmlBody, CommonVariables, customerAudience);

    [GeneratedRegex(@"\{\{\s*([a-z_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
