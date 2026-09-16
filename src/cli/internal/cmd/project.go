package cmd

import (
	"net/http"

	"github.com/spf13/cobra"
)

func (app *application) newProjectCommand() *cobra.Command {
	project := &cobra.Command{Use: "project", Short: "Work with projects"}
	project.AddCommand(
		app.newProjectListCommand(),
		app.newProjectCreateCommand(),
		app.newProjectUpdateCommand(),
		app.newInstructionsCommand(),
		app.newEmailCommand(),
	)
	return project
}

func (app *application) newProjectListCommand() *cobra.Command {
	return &cobra.Command{
		Use:   "list",
		Short: "List projects visible to this agent",
		Args:  cobra.NoArgs,
		RunE: func(command *cobra.Command, _ []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodGet, "/api/backoffice/projects", nil, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
}

func (app *application) newProjectCreateCommand() *cobra.Command {
	var key, name string
	command := &cobra.Command{
		Use:   "create",
		Short: "Create a project as an administrator agent",
		Args:  cobra.NoArgs,
		RunE: func(command *cobra.Command, _ []string) error {
			if key == "" || name == "" {
				return &exitError{code: 2, message: "--key and --name are required"}
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPost, "/api/backoffice/projects", map[string]any{"key": key, "name": name}, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&key, "key", "", "short project key")
	command.Flags().StringVar(&name, "name", "", "project name")
	return command
}

func (app *application) newProjectUpdateCommand() *cobra.Command {
	var key, name string
	command := &cobra.Command{
		Use:   "update PROJECT_ID",
		Short: "Change a project as an administrator agent",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			if key == "" && name == "" {
				return &exitError{code: 2, message: "--key or --name is required"}
			}
			payload := map[string]any{}
			if key != "" {
				payload["key"] = key
			}
			if name != "" {
				payload["name"] = name
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPatch, "/api/backoffice/projects/"+args[0], payload, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&key, "key", "", "new project key")
	command.Flags().StringVar(&name, "name", "", "new project name")
	return command
}

func (app *application) newInstructionsCommand() *cobra.Command {
	instructions := &cobra.Command{Use: "instructions", Short: "Read or update project support instructions"}
	instructions.AddCommand(app.newInstructionsGetCommand(), app.newInstructionsSetCommand())
	return instructions
}

func (app *application) newInstructionsGetCommand() *cobra.Command {
	return &cobra.Command{
		Use:   "get PROJECT_ID",
		Short: "Read project support instructions",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodGet, "/api/backoffice/projects/"+args[0]+"/support-instructions", nil, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
}

func (app *application) newInstructionsSetCommand() *cobra.Command {
	var inline, file string
	command := &cobra.Command{
		Use:   "set PROJECT_ID",
		Short: "Replace support instructions as an administrator agent",
		Args:  cobra.ExactArgs(1),
		RunE: func(command *cobra.Command, args []string) error {
			markdown, err := readText(inline, file, "instructions", "instructions-file", command.InOrStdin())
			if err != nil {
				return err
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request(http.MethodPut, "/api/backoffice/projects/"+args[0]+"/support-instructions", map[string]any{"markdown": markdown}, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
	command.Flags().StringVar(&inline, "instructions", "", "short inline Markdown")
	command.Flags().StringVar(&file, "instructions-file", "", "read Markdown from a file or - for stdin")
	return command
}
