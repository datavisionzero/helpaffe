package cmd

import (
	"fmt"
	"io"

	"github.com/spf13/cobra"
)

func New(version string) *cobra.Command {
	root := &cobra.Command{
		Use:           "helpaffe",
		Short:         "Work with a helpaffe helpdesk",
		SilenceErrors: true,
		SilenceUsage:  true,
	}
	root.SetVersionTemplate("helpaffe {{.Version}}\n")
	root.Version = version
	root.AddCommand(newVersionCommand(version))
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

func ExecuteForTest(command *cobra.Command, output io.Writer, args ...string) error {
	command.SetOut(output)
	command.SetErr(output)
	command.SetArgs(args)
	return command.Execute()
}
