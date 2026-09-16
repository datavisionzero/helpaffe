package cmd

import (
	"net/http"
	"net/url"
	"strconv"

	"github.com/spf13/cobra"
)

func (app *application) newTicketCommand() *cobra.Command {
	ticket := &cobra.Command{Use: "ticket", Short: "Work with support tickets"}
	ticket.AddCommand(
		app.newTicketListCommand(),
		app.newTicketGetCommand(),
		app.newTicketRequesterTicketsCommand(),
		app.newTicketNextCommand(),
		app.newTicketReplyCommand(),
		app.newTicketNoteCommand(),
		app.newTicketUpdateCommand(),
		app.newTicketStatusCommand("resolve", "resolved"),
		app.newTicketStatusCommand("reopen", "open"),
		app.newTicketNotificationCommand(),
	)
	return ticket
}

func (app *application) newTicketRequesterTicketsCommand() *cobra.Command {
	var status, cursor string
	var limit int
	command := &cobra.Command{
		Use:     "requester-tickets NUMBER",
		Aliases: []string{"related"},
		Short:   "List other tickets from this requester in the same project",
		Args:    cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			query := url.Values{}
			setQuery(query, "status", status)
			setQuery(query, "cursor", cursor)
			if limit != 50 {
				query.Set("limit", strconv.Itoa(limit))
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			path := "/api/backoffice/tickets/" + url.PathEscape(args[0]) + "/requester-tickets"
			if encoded := query.Encode(); encoded != "" {
				path += "?" + encoded
			}
			body, _, err := client.request(http.MethodGet, path, nil, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&status, "status", "", "filter by ticket status")
	command.Flags().IntVar(&limit, "limit", 50, "page size from 1 to 100")
	command.Flags().StringVar(&cursor, "cursor", "", "opaque cursor from the previous page")
	return command
}

func (app *application) newTicketNotificationCommand() *cobra.Command {
	notification := &cobra.Command{Use: "notification", Short: "Work with ticket email deliveries"}
	notification.AddCommand(&cobra.Command{
		Use:   "retry NUMBER NOTIFICATION_ID",
		Short: "Queue a failed email delivery for another attempt",
		Args:  cobra.ExactArgs(2),
		RunE: func(command *cobra.Command, args []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			key, err := requestKey()
			if err != nil {
				return err
			}
			path := "/api/backoffice/tickets/" + url.PathEscape(args[0]) + "/notifications/" + url.PathEscape(args[1]) + "/retry"
			body, _, err := client.request(http.MethodPost, path, nil, 0, key)
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	})
	return notification
}

func (app *application) newTicketListCommand() *cobra.Command {
	var project, status, priority, assignee, search, cursor string
	var mine bool
	var limit int
	command := &cobra.Command{
		Use:   "list",
		Short: "List and search visible tickets",
		Args:  cobra.NoArgs,
		RunE: func(command *cobra.Command, _ []string) error {
			query := url.Values{}
			setQuery(query, "project_id", project)
			setQuery(query, "status", status)
			setQuery(query, "priority", priority)
			setQuery(query, "assignee_id", assignee)
			setQuery(query, "search", search)
			setQuery(query, "cursor", cursor)
			if mine {
				query.Set("mine", "true")
			}
			if limit != 50 {
				query.Set("limit", strconv.Itoa(limit))
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			path := "/api/backoffice/tickets"
			if encoded := query.Encode(); encoded != "" {
				path += "?" + encoded
			}
			body, _, err := client.request(http.MethodGet, path, nil, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&project, "project", "", "project id")
	command.Flags().StringVar(&status, "status", "", "open, in_progress, waiting_for_customer, or resolved")
	command.Flags().StringVar(&priority, "priority", "", "normal or urgent")
	command.Flags().StringVar(&assignee, "assignee", "", "assignee user id")
	command.Flags().BoolVar(&mine, "mine", false, "only tickets assigned to the responsible user")
	command.Flags().StringVar(&search, "search", "", "search number, subject, requester name, or email")
	command.Flags().IntVar(&limit, "limit", 50, "page size from 1 to 100")
	command.Flags().StringVar(&cursor, "cursor", "", "opaque cursor from the previous page")
	return command
}

func (app *application) newTicketGetCommand() *cobra.Command {
	return &cobra.Command{
		Use:     "get NUMBER",
		Aliases: []string{"context"},
		Short:   "Read a ticket with conversation and support instructions",
		Args:    cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodGet, "/api/backoffice/tickets/"+url.PathEscape(args[0]), nil, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
}

func (app *application) newTicketNextCommand() *cobra.Command {
	var project string
	command := &cobra.Command{
		Use:   "next",
		Short: "Atomically acquire the next eligible ticket",
		Args:  cobra.NoArgs,
		RunE: func(command *cobra.Command, _ []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			key, err := requestKey()
			if err != nil {
				return err
			}
			payload := map[string]any{"project_id": nil}
			if project != "" {
				payload["project_id"] = project
			}
			body, _, err := client.request(http.MethodPost, "/api/backoffice/tickets/next", payload, 0, key)
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&project, "project", "", "limit acquisition to one project id")
	return command
}

func (app *application) newTicketReplyCommand() *cobra.Command {
	var message, messageFile, status string
	var version int
	command := &cobra.Command{
		Use:   "reply NUMBER",
		Short: "Send a public reply and set the resulting status",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if version < 1 || status == "" {
				return &exitError{code: 2, message: "--version and --status are required"}
			}
			content, err := readText(message, messageFile, "message", "message-file", command.InOrStdin())
			if err != nil {
				return err
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			key, err := requestKey()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPost, "/api/backoffice/tickets/"+url.PathEscape(args[0])+"/replies", map[string]any{
				"message": content,
				"status":  status,
			}, version, key)
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().IntVar(&version, "version", 0, "last-read positive ticket version")
	command.Flags().StringVar(&status, "status", "", "in_progress, waiting_for_customer, or resolved")
	command.Flags().StringVar(&message, "message", "", "short inline reply")
	command.Flags().StringVar(&messageFile, "message-file", "", "read the reply from a file or - for stdin")
	return command
}

func (app *application) newTicketNoteCommand() *cobra.Command {
	var note, noteFile string
	var version int
	command := &cobra.Command{
		Use:   "note NUMBER",
		Short: "Add an internal support note",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if version < 1 {
				return &exitError{code: 2, message: "--version is required"}
			}
			content, err := readText(note, noteFile, "note", "note-file", command.InOrStdin())
			if err != nil {
				return err
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			key, err := requestKey()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPost, "/api/backoffice/tickets/"+url.PathEscape(args[0])+"/notes", map[string]any{"message": content}, version, key)
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().IntVar(&version, "version", 0, "last-read positive ticket version")
	command.Flags().StringVar(&note, "note", "", "short inline note")
	command.Flags().StringVar(&noteFile, "note-file", "", "read the note from a file or - for stdin")
	return command
}

func (app *application) newTicketUpdateCommand() *cobra.Command {
	var status, priority, assignee string
	var clearAssignee bool
	var version int
	command := &cobra.Command{
		Use:   "update NUMBER",
		Short: "Change ticket status, priority, or assignee",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if version < 1 {
				return &exitError{code: 2, message: "--version is required"}
			}
			if status == "" && priority == "" && assignee == "" && !clearAssignee {
				return &exitError{code: 2, message: "at least one update flag is required"}
			}
			if assignee != "" && clearAssignee {
				return &exitError{code: 2, message: "use --assignee or --clear-assignee, not both"}
			}
			payload := map[string]any{}
			setPayload(payload, "status", status)
			setPayload(payload, "priority", priority)
			setPayload(payload, "assignee_id", assignee)
			if clearAssignee {
				payload["clear_assignee"] = true
			}
			return app.updateTicket(command, args[0], version, payload)
		},
	}
	command.Flags().IntVar(&version, "version", 0, "last-read positive ticket version")
	command.Flags().StringVar(&status, "status", "", "new status")
	command.Flags().StringVar(&priority, "priority", "", "normal or urgent")
	command.Flags().StringVar(&assignee, "assignee", "", "eligible support user id")
	command.Flags().BoolVar(&clearAssignee, "clear-assignee", false, "remove the current assignee")
	return command
}

func (app *application) newTicketStatusCommand(name, status string) *cobra.Command {
	var version int
	command := &cobra.Command{
		Use:   name + " NUMBER",
		Short: name + " a ticket",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if version < 1 {
				return &exitError{code: 2, message: "--version is required"}
			}
			return app.updateTicket(command, args[0], version, map[string]any{"status": status})
		},
	}
	command.Flags().IntVar(&version, "version", 0, "last-read positive ticket version")
	return command
}

func (app *application) updateTicket(command *cobra.Command, number string, version int, payload map[string]any) error {
	client, err := app.apiClient()
	if err != nil {
		return err
	}
	body, _, err := client.request(http.MethodPatch, "/api/backoffice/tickets/"+url.PathEscape(number), payload, version, "")
	if err != nil {
		return err
	}
	return app.write(command, body)
}

func setQuery(values url.Values, key, value string) {
	if value != "" {
		values.Set(key, value)
	}
}

func setPayload(values map[string]any, key, value string) {
	if value != "" {
		values[key] = value
	}
}
