package cmd

import (
	"encoding/json"
	"fmt"
	"os"

	"github.com/spf13/cobra"
)

func (app *application) newStatusCommand() *cobra.Command {
	return &cobra.Command{
		Use:   "status",
		Short: "Show configuration sources without revealing credentials",
		Args:  cobra.NoArgs,
		RunE: func(command *cobra.Command, _ []string) error {
			urlSource := "missing"
			if app.url != "" {
				urlSource = "--url"
			} else if os.Getenv("HELPAFFE_URL") != "" {
				urlSource = "HELPAFFE_URL"
			}
			tokenSource := "missing"
			if os.Getenv("HELPAFFE_TOKEN") != "" {
				tokenSource = "HELPAFFE_TOKEN"
			} else if app.tokenFile != "" {
				tokenSource = "--token-file"
			}
			if app.json {
				body, _ := json.Marshal(map[string]string{"url_source": urlSource, "token_source": tokenSource})
				_, err := fmt.Fprintf(command.OutOrStdout(), "%s\n", body)
				return err
			}
			_, err := fmt.Fprintf(command.OutOrStdout(), "URL source: %s\nToken source: %s\n", urlSource, tokenSource)
			return err
		},
	}
}

func (app *application) newMeCommand() *cobra.Command {
	return &cobra.Command{
		Use:   "me",
		Short: "Show the responsible user and acting agent",
		Args:  cobra.NoArgs,
		RunE: func(command *cobra.Command, _ []string) error {
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			body, _, err := client.request("GET", "/api/backoffice/me", nil, 0, "")
			if err != nil {
				return err
			}
			return app.write(command, body)
		},
	}
}
