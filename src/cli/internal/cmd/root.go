package cmd

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"

	"github.com/spf13/cobra"
)

type application struct {
	version      string
	url          string
	tokenFile    string
	insecureHTTP bool
	json         bool
}

func New(version string) *cobra.Command {
	app := &application{version: version}
	root := &cobra.Command{
		Use:           "helpaffe",
		Short:         "Work with a helpaffe helpdesk",
		SilenceErrors: true,
		SilenceUsage:  true,
	}
	root.SetVersionTemplate("helpaffe {{.Version}}\n")
	root.Version = version
	root.PersistentFlags().StringVar(&app.url, "url", "", "helpaffe instance URL (or HELPAFFE_URL)")
	root.PersistentFlags().StringVar(&app.tokenFile, "token-file", "", "read the agent token from a mode-0600 file")
	root.PersistentFlags().BoolVar(&app.insecureHTTP, "insecure-http", false, "allow plain HTTP outside loopback")
	root.PersistentFlags().BoolVar(&app.json, "json", false, "write exactly one JSON value")
	root.AddCommand(
		newVersionCommand(version),
		app.newStatusCommand(),
		app.newMeCommand(),
		app.newProjectCommand(),
		app.newSolutionCommand(),
		app.newTicketCommand(),
	)
	return root
}

func newVersionCommand(version string) *cobra.Command {
	return &cobra.Command{
		Use:   "version",
		Short: "Print the CLI version",
		Args:  cobra.NoArgs,
		RunE: func(command *cobra.Command, _ []string) error {
			_, err := fmt.Fprintf(command.OutOrStdout(), "helpaffe %s\n", version)
			return err
		},
	}
}

func (app *application) write(command *cobra.Command, body []byte) error {
	if app.json {
		var value any
		if err := json.Unmarshal(body, &value); err != nil {
			return &exitError{code: 1, message: "server returned invalid JSON"}
		}
		encoded, err := json.Marshal(value)
		if err != nil {
			return err
		}
		_, err = fmt.Fprintf(command.OutOrStdout(), "%s\n", encoded)
		return err
	}
	var formatted any
	if err := json.Unmarshal(body, &formatted); err != nil {
		return &exitError{code: 1, message: "server returned invalid JSON"}
	}
	encoded, err := json.MarshalIndent(formatted, "", "  ")
	if err != nil {
		return err
	}
	_, err = fmt.Fprintf(command.OutOrStdout(), "%s\n", encoded)
	return err
}

type exitError struct {
	code    int
	message string
}

func (err *exitError) Error() string { return err.message }

func ExitCode(err error) int {
	var coded *exitError
	if errors.As(err, &coded) {
		return coded.code
	}
	return 1
}

func PrintError(root *cobra.Command, output io.Writer, err error) {
	var api *apiError
	var waitTimeout *waitTimeoutError
	jsonOutput, _ := root.Flags().GetBool("json")
	if errors.As(err, &api) && jsonOutput && len(api.body) > 0 {
		fmt.Fprintf(output, "%s\n", api.body)
		return
	}
	if errors.As(err, &waitTimeout) && jsonOutput && len(waitTimeout.body) > 0 {
		fmt.Fprintf(output, "%s\n", waitTimeout.body)
		return
	}
	if jsonOutput {
		_ = json.NewEncoder(output).Encode(struct {
			Type     string `json:"type"`
			Title    string `json:"title"`
			Detail   string `json:"detail"`
			ExitCode int    `json:"exit_code"`
		}{
			Type:     "/problems/cli",
			Title:    "CLI error",
			Detail:   err.Error(),
			ExitCode: ExitCode(err),
		})
		return
	}
	fmt.Fprintf(output, "helpaffe: %s\n", err)
}

func ExecuteForTest(command *cobra.Command, output io.Writer, args ...string) error {
	command.SetOut(output)
	command.SetErr(output)
	command.SetArgs(args)
	return command.Execute()
}
