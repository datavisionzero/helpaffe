package cmd

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
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

func TestTicketNotificationRetryUsesIdempotencyWithoutTicketVersion(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/HLP-42/notifications/018f6b45-9e25-7def-a000-112233445566/retry"; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		if request.Method != http.MethodPost {
			t.Errorf("method = %q", request.Method)
		}
		if request.Header.Get("Idempotency-Key") == "" {
			t.Error("Idempotency-Key is missing")
		}
		if value := request.Header.Get("If-Match"); value != "" {
			t.Errorf("If-Match = %q, want empty", value)
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, `{ "status": "pending", "attempt_count": 0 }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	var output bytes.Buffer
	err := ExecuteForTest(New("1.2.3"), &output, "--json", "ticket", "notification", "retry", "HLP-42", "018f6b45-9e25-7def-a000-112233445566")
	if err != nil {
		t.Fatal(err)
	}
	if got, want := output.String(), `{"attempt_count":0,"status":"pending"}`+"\n"; got != want {
		t.Fatalf("output = %q, want %q", got, want)
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

func TestAdministratorAgentManagesEmailSettingsTemplatesPreviewAndTestDelivery(t *testing.T) {
	received := map[string]map[string]any{}
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		var payload map[string]any
		if request.Method != http.MethodGet {
			if err := json.NewDecoder(request.Body).Decode(&payload); err != nil {
				t.Error(err)
			}
			received[request.URL.Path] = payload
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, `{ "status": "ok" }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")
	passwordFile := filepath.Join(t.TempDir(), "smtp-password")
	if err := os.WriteFile(passwordFile, []byte("smtp-secret\n"), 0o600); err != nil {
		t.Fatal(err)
	}

	settingsArgs := []string{"--json", "project", "email", "settings", "set", "project-1",
		"--smtp-host", "smtp.example.test", "--smtp-port", "587", "--smtp-password-file", passwordFile,
		"--sender-name", "Product", "--sender-email", "support@example.test",
		"--support-recipient", "team@example.test", "--brand-name", "Product",
		"--customer-ticket-link", "https://product.example.test/{{ticket_number}}",
		"--backoffice-ticket-link", "https://support.example.test/{{ticket_number}}"}
	if err := ExecuteForTest(New("1.2.3"), io.Discard, settingsArgs...); err != nil {
		t.Fatal(err)
	}
	if err := ExecuteForTest(New("1.2.3"), io.Discard, "--json", "project", "email", "template", "set", "project-1", "public_reply_customer",
		"--subject", "Reply {{ticket_number}}", "--text", "Text {{message}}", "--html", "<p>{{message}}</p>"); err != nil {
		t.Fatal(err)
	}
	if err := ExecuteForTest(New("1.2.3"), io.Discard, "--json", "project", "email", "template", "preview", "project-1", "public_reply_customer",
		"--ticket-number", "HLP-42", "--message", "Preview"); err != nil {
		t.Fatal(err)
	}
	if err := ExecuteForTest(New("1.2.3"), io.Discard, "--json", "project", "email", "test", "project-1",
		"--recipient", "recipient@example.test", "--template", "public_reply_customer"); err != nil {
		t.Fatal(err)
	}

	settings := received["/api/backoffice/projects/project-1/email-settings"]
	if got := settings["language"]; got != "en" {
		t.Fatalf("language = %v", got)
	}
	smtp := settings["smtp"].(map[string]any)
	if got := smtp["password"]; got != "smtp-secret" {
		t.Fatalf("SMTP password = %v", got)
	}
	template := received["/api/backoffice/projects/project-1/email-templates/public_reply_customer"]
	if got := template["text_body"]; got != "Text {{message}}" {
		t.Fatalf("text body = %v", got)
	}
	preview := received["/api/backoffice/projects/project-1/email-templates/public_reply_customer/preview"]
	if got := preview["ticket_number"]; got != "HLP-42" {
		t.Fatalf("preview ticket number = %v", got)
	}
	testSend := received["/api/backoffice/projects/project-1/email/test"]
	if got := testSend["recipient"]; got != "recipient@example.test" {
		t.Fatalf("recipient = %v", got)
	}
}
