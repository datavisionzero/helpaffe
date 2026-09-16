package cmd

import (
	"encoding/json"
	"net/http"
	"net/url"
	"strconv"

	"github.com/spf13/cobra"
)

func (app *application) newSolutionCommand() *cobra.Command {
	solution := &cobra.Command{Use: "solution", Short: "Work with internal project solution articles"}
	solution.AddCommand(
		app.newSolutionListCommand(),
		app.newSolutionGetCommand(),
		app.newSolutionCreateCommand(),
		app.newSolutionUpdateCommand(),
		app.newSolutionDeleteCommand(),
	)
	return solution
}

func (app *application) newSolutionListCommand() *cobra.Command {
	var search, cursor string
	var limit int
	command := &cobra.Command{
		Use:   "list PROJECT_ID",
		Short: "List or search internal solution articles in a project",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if limit < 1 || limit > 100 {
				return &exitError{code: 2, message: "--limit must be between 1 and 100"}
			}
			query := url.Values{}
			setQuery(query, "search", search)
			setQuery(query, "cursor", cursor)
			if limit != 50 {
				query.Set("limit", strconv.Itoa(limit))
			}
			path := solutionCollectionPath(args[0])
			if encoded := query.Encode(); encoded != "" {
				path += "?" + encoded
			}
			return app.requestAndWrite(command, http.MethodGet, path, nil, 0, "")
		},
	}
	command.Flags().StringVar(&search, "search", "", "full-text search over article title and Markdown")
	command.Flags().IntVar(&limit, "limit", 50, "page size from 1 to 100")
	command.Flags().StringVar(&cursor, "cursor", "", "opaque cursor from the previous page")
	return command
}

func (app *application) newSolutionGetCommand() *cobra.Command {
	return &cobra.Command{
		Use:   "get PROJECT_ID KEY",
		Short: "Read one internal solution article",
		Args:  cobra.ExactArgs(2),
		RunE: func(command *cobra.Command, args []string) error {
			return app.requestAndWrite(command, http.MethodGet, solutionPath(args[0], args[1]), nil, 0, "")
		},
	}
}

func (app *application) newSolutionCreateCommand() *cobra.Command {
	var key, title, markdown, markdownFile string
	command := &cobra.Command{
		Use:   "create PROJECT_ID",
		Short: "Create an internal Markdown solution article",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if key == "" || title == "" {
				return &exitError{code: 2, message: "--key and --title are required"}
			}
			content, err := readText(markdown, markdownFile, "markdown", "markdown-file", command.InOrStdin())
			if err != nil {
				return err
			}
			requestID, err := requestKey()
			if err != nil {
				return err
			}
			return app.requestAndWrite(command, http.MethodPost, solutionCollectionPath(args[0]), map[string]any{
				"key": key, "title": title, "markdown": content,
			}, 0, requestID)
		},
	}
	command.Flags().StringVar(&key, "key", "", "stable project-local article key")
	command.Flags().StringVar(&title, "title", "", "short article title")
	command.Flags().StringVar(&markdown, "markdown", "", "article Markdown")
	command.Flags().StringVar(&markdownFile, "markdown-file", "", "read article Markdown from a file, or - for stdin")
	return command
}

func (app *application) newSolutionUpdateCommand() *cobra.Command {
	var title, markdown, markdownFile string
	var version int
	command := &cobra.Command{
		Use:   "update PROJECT_ID KEY",
		Short: "Replace an internal solution article",
		Args:  cobra.ExactArgs(2),
		RunE: func(command *cobra.Command, args []string) error {
			if version < 1 || title == "" {
				return &exitError{code: 2, message: "--version and --title are required"}
			}
			content, err := readText(markdown, markdownFile, "markdown", "markdown-file", command.InOrStdin())
			if err != nil {
				return err
			}
			requestID, err := requestKey()
			if err != nil {
				return err
			}
			return app.requestAndWrite(command, http.MethodPut, solutionPath(args[0], args[1]), map[string]any{
				"title": title, "markdown": content,
			}, version, requestID)
		},
	}
	command.Flags().IntVar(&version, "version", 0, "last-read positive solution version")
	command.Flags().StringVar(&title, "title", "", "new article title")
	command.Flags().StringVar(&markdown, "markdown", "", "new article Markdown")
	command.Flags().StringVar(&markdownFile, "markdown-file", "", "read new article Markdown from a file, or - for stdin")
	return command
}

func (app *application) newSolutionDeleteCommand() *cobra.Command {
	var version int
	command := &cobra.Command{
		Use:   "delete PROJECT_ID KEY",
		Short: "Delete an obsolete internal solution article",
		Args:  cobra.ExactArgs(2),
		RunE: func(command *cobra.Command, args []string) error {
			if version < 1 {
				return &exitError{code: 2, message: "--version is required"}
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			requestID, err := requestKey()
			if err != nil {
				return err
			}
			if _, _, err := client.request(http.MethodDelete, solutionPath(args[0], args[1]), nil, version, requestID); err != nil {
				return err
			}
			body, err := json.Marshal(map[string]any{"deleted": true, "key": args[1]})
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().IntVar(&version, "version", 0, "last-read positive solution version")
	return command
}

func (app *application) requestAndWrite(
	command *cobra.Command,
	method string,
	path string,
	payload any,
	version int,
	idempotencyKey string,
) error {
	client, err := app.apiClient()
	if err != nil {
		return err
	}
	body, _, err := client.request(method, path, payload, version, idempotencyKey)
	if err != nil {
		return err
	}
	return app.write(command, body)
}

func solutionCollectionPath(projectID string) string {
	return "/api/backoffice/projects/" + url.PathEscape(projectID) + "/solutions"
}

func solutionPath(projectID, key string) string {
	return solutionCollectionPath(projectID) + "/" + url.PathEscape(key)
}
