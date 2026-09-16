package cmd

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
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

func TestTicketNoteUsesAgentAuthConcurrencyAndStdin(t *testing.T) {
	var received map[string]any
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.URL.Path != "/api/backoffice/tickets/HLP-42/notes" {
			t.Errorf("path = %q", request.URL.Path)
		}
		if got, want := request.Header.Get("Authorization"), "Bearer hfa_test-token"; got != want {
			t.Errorf("authorization = %q, want %q", got, want)
		}
		if got, want := request.Header.Get("If-Match"), `"7"`; got != want {
			t.Errorf("If-Match = %q, want %q", got, want)
		}
		if request.Header.Get("Idempotency-Key") == "" {
			t.Error("Idempotency-Key is missing")
		}
		if err := json.NewDecoder(request.Body).Decode(&received); err != nil {
			t.Error(err)
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, `{ "summary": { "version": 8 } }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	root := New("1.2.3-dev")
	root.SetIn(strings.NewReader("line one\r\nline two\r\n"))
	var output bytes.Buffer
	if err := ExecuteForTest(root, &output, "--json", "ticket", "note", "HLP-42", "--version", "7", "--note-file", "-"); err != nil {
		t.Fatal(err)
	}
	if got, want := output.String(), `{"summary":{"version":8}}`+"\n"; got != want {
		t.Fatalf("output = %q, want %q", got, want)
	}
	if got, want := received["message"], "line one\nline two\n"; got != want {
		t.Fatalf("message = %q, want %q", got, want)
	}
}

func TestVersionConflictHasStableExitCodeAndMachineError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, _ *http.Request) {
		writer.Header().Set("Content-Type", "application/problem+json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		writer.WriteHeader(http.StatusPreconditionFailed)
		_, _ = io.WriteString(writer, `{"type":"/problems/stale","detail":"The ticket changed.","current_version":9}`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	root := New("1.2.3")
	var output bytes.Buffer
	err := ExecuteForTest(root, &output, "--json", "ticket", "reopen", "HLP-42", "--version", "7")
	if err == nil {
		t.Fatal("expected a version conflict")
	}
	if got, want := ExitCode(err), 6; got != want {
		t.Fatalf("exit code = %d, want %d", got, want)
	}
	var printed bytes.Buffer
	PrintError(root, &printed, err)
	if got, want := printed.String(), `{"type":"/problems/stale","detail":"The ticket changed.","current_version":9}`+"\n"; got != want {
		t.Fatalf("error output = %q, want %q", got, want)
	}
}

func TestStatusDoesNotRevealToken(t *testing.T) {
	t.Setenv("HELPAFFE_URL", "https://support.example.test")
	t.Setenv("HELPAFFE_TOKEN", "hfa_do-not-print-this")
	var output bytes.Buffer
	if err := ExecuteForTest(New("1.2.3"), &output, "--json", "status"); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(output.String(), "do-not-print-this") {
		t.Fatalf("status revealed the token: %s", output.String())
	}
	if got, want := output.String(), `{"token_source":"HELPAFFE_TOKEN","url_source":"HELPAFFE_URL"}`+"\n"; got != want {
		t.Fatalf("output = %q, want %q", got, want)
	}
}

func TestRejectsProductCredential(t *testing.T) {
	t.Setenv("HELPAFFE_URL", "https://support.example.test")
	t.Setenv("HELPAFFE_TOKEN", "hfp_product-key")
	err := ExecuteForTest(New("1.2.3"), io.Discard, "me")
	if got, want := ExitCode(err), 2; got != want {
		t.Fatalf("exit code = %d, want %d", got, want)
	}
	root := New("1.2.3")
	root.SetArgs([]string{"--json", "me"})
	err = root.Execute()
	var printed bytes.Buffer
	PrintError(root, &printed, err)
	var problem struct {
		Type     string `json:"type"`
		ExitCode int    `json:"exit_code"`
	}
	if decodeErr := json.Unmarshal(printed.Bytes(), &problem); decodeErr != nil {
		t.Fatal(decodeErr)
	}
	if problem.Type != "/problems/cli" || problem.ExitCode != 2 {
		t.Fatalf("problem = %#v", problem)
	}
}
