package cmd

import (
	"fmt"
	"io"
	"os"
	"strings"
)

func readText(inline, path, inlineName, fileName string, stdin io.Reader) (string, error) {
	if inline != "" && path != "" {
		return "", &exitError{code: 2, message: fmt.Sprintf("use either --%s or --%s, not both", inlineName, fileName)}
	}
	var content []byte
	var err error
	switch {
	case path == "-":
		content, err = io.ReadAll(stdin)
	case path != "":
		content, err = os.ReadFile(path)
	case inline != "":
		content = []byte(inline)
	default:
		return "", &exitError{code: 2, message: fmt.Sprintf("--%s or --%s is required", inlineName, fileName)}
	}
	if err != nil {
		return "", &exitError{code: 2, message: err.Error()}
	}
	value := strings.ReplaceAll(string(content), "\r\n", "\n")
	value = strings.ReplaceAll(value, "\r", "\n")
	if strings.TrimSpace(value) == "" {
		return "", &exitError{code: 2, message: "text input must not be empty"}
	}
	return value, nil
}
