package cmd

import (
	"net/http"
	"os"
	"strings"

	"github.com/spf13/cobra"
)

func (app *application) newEmailCommand() *cobra.Command {
	email := &cobra.Command{Use: "email", Short: "Manage project email settings and templates"}
	email.AddCommand(
		app.newEmailSettingsCommand(),
		app.newEmailTemplateCommand(),
		app.newEmailTestCommand(),
	)
	return email
}

func (app *application) newEmailSettingsCommand() *cobra.Command {
	settings := &cobra.Command{Use: "settings", Short: "Read or update project SMTP, sender, branding, and links"}
	settings.AddCommand(app.newEmailSettingsGetCommand(), app.newEmailSettingsSetCommand())
	return settings
}

func (app *application) newEmailSettingsGetCommand() *cobra.Command {
	return &cobra.Command{
		Use:   "get PROJECT_ID",
		Short: "Read project email settings without revealing the SMTP password",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodGet, "/api/backoffice/projects/"+args[0]+"/email-settings", nil, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
}

func (app *application) newEmailSettingsSetCommand() *cobra.Command {
	var smtpHost, smtpUsername, smtpPasswordFile, senderName, senderEmail string
	var brandName, brandLogoURL, brandColor, customerLink, backofficeLink string
	var smtpPort int
	var smtpTLS bool
	var supportRecipients []string
	command := &cobra.Command{
		Use:   "set PROJECT_ID",
		Short: "Replace project email settings as an administrator agent",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if smtpHost == "" || smtpPort < 1 || senderName == "" || senderEmail == "" || len(supportRecipients) == 0 ||
				brandName == "" || customerLink == "" || backofficeLink == "" {
				return &exitError{code: 2, message: "SMTP host/port, sender, at least one support recipient, brand name, and both ticket links are required"}
			}
			var password any
			if smtpPasswordFile != "" {
				content, err := os.ReadFile(smtpPasswordFile)
				if err != nil {
					return &exitError{code: 2, message: err.Error()}
				}
				value := strings.TrimSpace(string(content))
				if value == "" {
					return &exitError{code: 2, message: "SMTP password file must not be empty"}
				}
				password = value
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPut, "/api/backoffice/projects/"+args[0]+"/email-settings", map[string]any{
				"language":           "en",
				"smtp":               map[string]any{"host": smtpHost, "port": smtpPort, "use_tls": smtpTLS, "username": emptyString(smtpUsername), "password": password},
				"sender":             map[string]any{"name": senderName, "email": senderEmail},
				"support_recipients": supportRecipients,
				"branding":           map[string]any{"name": brandName, "logo_url": emptyString(brandLogoURL), "color": brandColor},
				"ticket_links":       map[string]any{"customer": customerLink, "backoffice": backofficeLink},
			}, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&smtpHost, "smtp-host", "", "SMTP host")
	command.Flags().IntVar(&smtpPort, "smtp-port", 587, "SMTP port")
	command.Flags().BoolVar(&smtpTLS, "smtp-tls", true, "enable SMTP TLS")
	command.Flags().StringVar(&smtpUsername, "smtp-username", "", "SMTP username")
	command.Flags().StringVar(&smtpPasswordFile, "smtp-password-file", "", "read the SMTP password from a file")
	command.Flags().StringVar(&senderName, "sender-name", "", "sender display name")
	command.Flags().StringVar(&senderEmail, "sender-email", "", "sender email address")
	command.Flags().StringSliceVar(&supportRecipients, "support-recipient", nil, "support recipient email; repeat or comma-separate")
	command.Flags().StringVar(&brandName, "brand-name", "", "email brand name")
	command.Flags().StringVar(&brandLogoURL, "brand-logo-url", "", "absolute logo URL; uploads are not supported")
	command.Flags().StringVar(&brandColor, "brand-color", "#2F6FED", "six-digit hexadecimal brand color")
	command.Flags().StringVar(&customerLink, "customer-ticket-link", "", "customer ticket URL containing {{ticket_number}}")
	command.Flags().StringVar(&backofficeLink, "backoffice-ticket-link", "", "backoffice ticket URL containing {{ticket_number}}")
	return command
}

func (app *application) newEmailTemplateCommand() *cobra.Command {
	template := &cobra.Command{Use: "template", Short: "List, read, update, or preview English email templates"}
	template.AddCommand(
		app.newEmailTemplateListCommand(),
		app.newEmailTemplateGetCommand(),
		app.newEmailTemplateSetCommand(),
		app.newEmailTemplatePreviewCommand(),
	)
	return template
}

func (app *application) newEmailTemplateListCommand() *cobra.Command {
	return emailGetCommand(app, "list PROJECT_ID", "List effective project email templates", func(args []string) string {
		return "/api/backoffice/projects/" + args[0] + "/email-templates"
	}, 1)
}

func (app *application) newEmailTemplateGetCommand() *cobra.Command {
	return emailGetCommand(app, "get PROJECT_ID TYPE", "Read one effective project email template", func(args []string) string {
		return "/api/backoffice/projects/" + args[0] + "/email-templates/" + args[1]
	}, 2)
}

func emailGetCommand(app *application, use, short string, path func([]string) string, argumentCount int) *cobra.Command {
	return &cobra.Command{
		Use: use, Short: short, Args: cobra.ExactArgs(argumentCount),
		RunE: func(command *cobra.Command, args []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodGet, path(args), nil, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
}

func (app *application) newEmailTemplateSetCommand() *cobra.Command {
	var subject, text, textFile, html, htmlFile string
	command := &cobra.Command{
		Use:   "set PROJECT_ID TYPE",
		Short: "Create or replace one project email template",
		Args:  cobra.ExactArgs(2),
		RunE: func(command *cobra.Command, args []string) error {
			if subject == "" {
				return &exitError{code: 2, message: "--subject is required"}
			}
			textBody, err := readText(text, textFile, "text", "text-file", command.InOrStdin())
			if err != nil {
				return err
			}
			htmlBody, err := readText(html, htmlFile, "html", "html-file", command.InOrStdin())
			if err != nil {
				return err
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPut, "/api/backoffice/projects/"+args[0]+"/email-templates/"+args[1], map[string]any{
				"subject": subject, "text_body": textBody, "html_body": htmlBody,
			}, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&subject, "subject", "", "template subject")
	command.Flags().StringVar(&text, "text", "", "short inline text body")
	command.Flags().StringVar(&textFile, "text-file", "", "read text body from a file")
	command.Flags().StringVar(&html, "html", "", "short inline HTML body")
	command.Flags().StringVar(&htmlFile, "html-file", "", "read HTML body from a file")
	return command
}

type emailSampleFlags struct {
	customerName, customerEmail, ticketNumber, ticketSubject, message, assigneeName string
}

func (sample *emailSampleFlags) add(command *cobra.Command) {
	command.Flags().StringVar(&sample.customerName, "customer-name", "", "preview customer name")
	command.Flags().StringVar(&sample.customerEmail, "customer-email", "", "preview customer email")
	command.Flags().StringVar(&sample.ticketNumber, "ticket-number", "", "preview ticket number")
	command.Flags().StringVar(&sample.ticketSubject, "ticket-subject", "", "preview ticket subject")
	command.Flags().StringVar(&sample.message, "message", "", "preview message")
	command.Flags().StringVar(&sample.assigneeName, "assignee-name", "", "preview assignee name")
}

func (sample *emailSampleFlags) payload() map[string]any {
	return map[string]any{
		"customer_name": emptyString(sample.customerName), "customer_email": emptyString(sample.customerEmail),
		"ticket_number": emptyString(sample.ticketNumber), "ticket_subject": emptyString(sample.ticketSubject),
		"message": emptyString(sample.message), "assignee_name": emptyString(sample.assigneeName),
	}
}

func (app *application) newEmailTemplatePreviewCommand() *cobra.Command {
	var sample emailSampleFlags
	command := &cobra.Command{
		Use:   "preview PROJECT_ID TYPE",
		Short: "Render an email template with sample values",
		Args:  cobra.ExactArgs(2),
		RunE: func(command *cobra.Command, args []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPost, "/api/backoffice/projects/"+args[0]+"/email-templates/"+args[1]+"/preview", sample.payload(), 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	sample.add(command)
	return command
}

func (app *application) newEmailTestCommand() *cobra.Command {
	var recipient, templateType string
	var sample emailSampleFlags
	command := &cobra.Command{
		Use:   "test PROJECT_ID",
		Short: "Send one rendered test email through the project's SMTP server",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if recipient == "" || templateType == "" {
				return &exitError{code: 2, message: "--recipient and --template are required"}
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPost, "/api/backoffice/projects/"+args[0]+"/email/test", map[string]any{
				"recipient": recipient, "template_type": templateType, "sample": sample.payload(),
			}, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&recipient, "recipient", "", "test recipient email")
	command.Flags().StringVar(&templateType, "template", "", "notification template type")
	sample.add(command)
	return command
}

func emptyString(value string) any {
	if strings.TrimSpace(value) == "" {
		return nil
	}
	return value
}
