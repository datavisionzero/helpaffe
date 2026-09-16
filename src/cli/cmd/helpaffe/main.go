package main

import (
	"os"

	"github.com/datavisionzero/helpaffe/src/cli/internal/cmd"
)

var version = "0.0.0-dev"

func main() {
	root := cmd.New(version)
	if err := root.Execute(); err != nil {
		cmd.PrintError(root, os.Stderr, err)
		os.Exit(cmd.ExitCode(err))
	}
}
