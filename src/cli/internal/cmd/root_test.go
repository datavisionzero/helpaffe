package cmd

import (
	"bytes"
	"testing"
)

func TestVersion(t *testing.T) {
	var output bytes.Buffer
	if err := ExecuteForTest(New("1.2.3"), &output, "version"); err != nil {
		t.Fatal(err)
	}
	if got, want := output.String(), "helpaffe 1.2.3\n"; got != want {
		t.Fatalf("output = %q, want %q", got, want)
	}
}
