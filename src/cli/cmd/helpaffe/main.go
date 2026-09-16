package main

import (
	"fmt"
	"os"

	"github.com/datavisionzero/helpaffe/src/cli/internal/cmd"
)

var version = "0.0.0-dev"

func main() {
	if err := cmd.New(version).Execute(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
}
