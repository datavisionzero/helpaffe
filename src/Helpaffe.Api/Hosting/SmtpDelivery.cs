using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Helpaffe.Application.Notifications;
using Helpaffe.Infrastructure.Notifications;

namespace Helpaffe.Api.Hosting;

public static class SmtpDelivery
{
    public static async Task SendAsync(
        ProjectEmailSettingsRecord settings,
        SmtpSecretProtector? protector,
        string recipient,
        RenderedEmail email,
        CancellationToken cancellationToken)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(settings.SenderEmail, settings.SenderName, Encoding.UTF8),
            Subject = email.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = email.TextBody,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(recipient));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(email.TextBody, Encoding.UTF8, MediaTypeNames.Text.Plain));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(email.HtmlBody, Encoding.UTF8, MediaTypeNames.Text.Html));

        using var smtp = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.SmtpUseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
        };
        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
        {
            if (settings.SmtpPasswordCiphertext is null || protector is null)
                throw new InvalidOperationException("SMTP credentials cannot be decrypted because the encryption key is unavailable.");
            smtp.Credentials = new NetworkCredential(settings.SmtpUsername, protector.Unprotect(settings.SmtpPasswordCiphertext));
        }
        await smtp.SendMailAsync(message, cancellationToken);
    }
}
