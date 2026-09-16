using Helpaffe.Application.Notifications;

namespace Helpaffe.UnitTests;

public sealed class EmailTemplateTests
{
    [Fact]
    public void Catalog_contains_the_five_fixed_English_notification_templates()
    {
        Assert.Equal(5, EmailTemplateCatalog.Types.Count);
        Assert.All(EmailTemplateCatalog.Types, type =>
        {
            var template = EmailTemplateCatalog.Get(type);
            Assert.False(string.IsNullOrWhiteSpace(template.Subject));
            Assert.False(string.IsNullOrWhiteSpace(template.TextBody));
            Assert.False(string.IsNullOrWhiteSpace(template.HtmlBody));
            Assert.Null(EmailTemplateCatalog.Validate(type, template.Subject, template.TextBody, template.HtmlBody));
        });
    }

    [Fact]
    public void Unknown_or_malformed_variables_are_rejected()
    {
        Assert.Contains("Unknown", EmailTemplateCatalog.Validate(
            "public_reply_customer",
            "Reply {{unknown_value}}",
            "Text",
            "<p>HTML</p>"));
        Assert.Contains("syntax", EmailTemplateCatalog.Validate(
            "public_reply_customer",
            "Reply {{ticket_number",
            "Text",
            "<p>HTML</p>"));
    }

    [Fact]
    public void Rendering_HTML_encodes_values_and_removes_subject_line_breaks()
    {
        var rendered = EmailTemplateCatalog.Render(
            "Reply {{ticket_number}}\nnow",
            "Hello {{customer_name}}",
            "<p>{{message}}</p>",
            new Dictionary<string, string>
            {
                ["ticket_number"] = "HLP-42",
                ["customer_name"] = "Avery <Customer>",
                ["message"] = "<script>alert('no')</script>",
            });

        Assert.Equal("Reply HLP-42 now", rendered.Subject);
        Assert.Equal("Hello Avery <Customer>", rendered.TextBody);
        Assert.DoesNotContain("<script>", rendered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", rendered.HtmlBody, StringComparison.Ordinal);
    }
}
