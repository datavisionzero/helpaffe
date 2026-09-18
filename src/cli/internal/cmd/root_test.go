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

func TestTicketReplyStreamsRepeatedAttachmentsAsMultipart(t *testing.T) {
	attachmentPath := filepath.Join(t.TempDir(), "evidence.txt")
	if err := os.WriteFile(attachmentPath, []byte("first attachment"), 0o600); err != nil {
		t.Fatal(err)
	}
	secondPath := filepath.Join(t.TempDir(), "details.json")
	if err := os.WriteFile(secondPath, []byte(`{"reproduced":true}`), 0o600); err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/HLP-42/replies"; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		if got, want := request.Header.Get("If-Match"), `"7"`; got != want {
			t.Errorf("If-Match = %q, want %q", got, want)
		}
		if request.Header.Get("Idempotency-Key") == "" {
			t.Error("Idempotency-Key is missing")
		}
		if err := request.ParseMultipartForm(maximumAttachmentTotal + 1024); err != nil {
			t.Fatal(err)
		}
		if got, want := request.FormValue("message"), "Public answer"; got != want {
			t.Errorf("message = %q, want %q", got, want)
		}
		if got, want := request.FormValue("status"), "waiting_for_customer"; got != want {
			t.Errorf("status = %q, want %q", got, want)
		}
		files := request.MultipartForm.File["files"]
		if got, want := len(files), 2; got != want {
			t.Fatalf("files = %d, want %d", got, want)
		}
		if got, want := files[0].Filename, "evidence.txt"; got != want {
			t.Errorf("first file = %q, want %q", got, want)
		}
		opened, err := files[1].Open()
		if err != nil {
			t.Fatal(err)
		}
		defer opened.Close()
		content, err := io.ReadAll(opened)
		if err != nil {
			t.Fatal(err)
		}
		if got, want := string(content), `{"reproduced":true}`; got != want {
			t.Errorf("second content = %q, want %q", got, want)
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, `{"summary":{"version":8},"conversation":[{"attachments":[{"file_name":"evidence.txt","is_public":true}]}]}`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	var output bytes.Buffer
	err := ExecuteForTest(New("1.2.3"), &output, "--json", "ticket", "reply", "HLP-42",
		"--version", "7", "--status", "waiting_for_customer", "--message", "Public answer",
		"--file", attachmentPath, "--file", secondPath)
	if err != nil {
		t.Fatal(err)
	}
	var result map[string]any
	if err := json.Unmarshal(output.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if result["conversation"] == nil {
		t.Fatal("machine-readable output omitted conversation attachment metadata")
	}
}

func TestTicketNoteUploadsAttachmentsOnlyToThePrivateNoteRoute(t *testing.T) {
	attachmentPath := filepath.Join(t.TempDir(), "private.txt")
	if err := os.WriteFile(attachmentPath, []byte("internal evidence"), 0o600); err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/HLP-42/notes"; got != want {
			t.Errorf("path = %q, want private note route %q", got, want)
		}
		if err := request.ParseMultipartForm(maximumAttachmentTotal + 1024); err != nil {
			t.Fatal(err)
		}
		if got := request.FormValue("status"); got != "" {
			t.Errorf("private note unexpectedly sent public reply status %q", got)
		}
		if got, want := len(request.MultipartForm.File["files"]), 1; got != want {
			t.Errorf("files = %d, want %d", got, want)
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, `{"summary":{"version":8}}`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	if err := ExecuteForTest(New("1.2.3"), io.Discard, "ticket", "note", "HLP-42",
		"--version", "7", "--note", "Private investigation", "--file", attachmentPath); err != nil {
		t.Fatal(err)
	}
}

func TestTicketAttachmentDownloadWritesANewFileAndCompleteJSON(t *testing.T) {
	const attachmentID = "018f6b45-9e25-7def-a000-112233445566"
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/HLP-42/attachments/"+attachmentID; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		writer.Header().Set("Content-Type", "text/plain")
		writer.Header().Set("Content-Disposition", `attachment; filename="evidence.txt"`)
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, "downloaded evidence")
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")
	outputPath := filepath.Join(t.TempDir(), "download.txt")

	var output bytes.Buffer
	err := ExecuteForTest(New("1.2.3"), &output, "--json", "ticket", "attachment", "download",
		"HLP-42", attachmentID, "--output", outputPath)
	if err != nil {
		t.Fatal(err)
	}
	content, err := os.ReadFile(outputPath)
	if err != nil {
		t.Fatal(err)
	}
	if got, want := string(content), "downloaded evidence"; got != want {
		t.Fatalf("content = %q, want %q", got, want)
	}
	var result struct {
		AttachmentID string `json:"attachment_id"`
		FileName     string `json:"file_name"`
		MediaType    string `json:"media_type"`
		Size         int64  `json:"size"`
		Path         string `json:"path"`
	}
	if err := json.Unmarshal(output.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if result.AttachmentID != attachmentID || result.FileName != "evidence.txt" || result.MediaType != "text/plain" || result.Size != int64(len(content)) || result.Path != outputPath {
		t.Fatalf("download result = %#v", result)
	}
}

func TestAttachmentValidationHasStableMachineReadableLocalErrors(t *testing.T) {
	attachmentPath := filepath.Join(t.TempDir(), "unsafe.exe")
	if err := os.WriteFile(attachmentPath, []byte("not allowed"), 0o600); err != nil {
		t.Fatal(err)
	}
	root := New("1.2.3")
	var output bytes.Buffer
	err := ExecuteForTest(root, &output, "--json", "ticket", "reply", "HLP-42", "--version", "7",
		"--status", "resolved", "--message", "Done", "--file", attachmentPath)
	if got, want := ExitCode(err), 4; got != want {
		t.Fatalf("exit code = %d, want %d", got, want)
	}
	if output.Len() != 0 {
		t.Fatalf("stdout = %q, want empty", output.String())
	}
	var printed bytes.Buffer
	PrintError(root, &printed, err)
	var problem struct {
		Type     string `json:"type"`
		ExitCode int    `json:"exit_code"`
	}
	if err := json.Unmarshal(printed.Bytes(), &problem); err != nil {
		t.Fatal(err)
	}
	if got, want := problem.Type, "/problems/cli/attachment-type"; got != want {
		t.Fatalf("type = %q, want %q", got, want)
	}
	if got, want := problem.ExitCode, 4; got != want {
		t.Fatalf("exit code field = %d, want %d", got, want)
	}
}

func TestAttachmentValidationSeparatesFileAndLimitProblems(t *testing.T) {
	emptyPath := filepath.Join(t.TempDir(), "empty.txt")
	if err := os.WriteFile(emptyPath, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	tests := []struct {
		name     string
		files    []string
		wantType string
		wantExit int
	}{
		{name: "missing file", files: []string{filepath.Join(t.TempDir(), "missing.txt")}, wantType: "/problems/cli/attachment-file", wantExit: 2},
		{name: "too many files", files: []string{"1.txt", "2.txt", "3.txt", "4.txt", "5.txt", "6.txt"}, wantType: "/problems/cli/attachment-limit", wantExit: 4},
		{name: "empty file", files: []string{emptyPath}, wantType: "/problems/cli/attachment-too-large", wantExit: 4},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			root := New("1.2.3")
			arguments := []string{"--json", "ticket", "note", "HLP-42", "--version", "7", "--note", "Private"}
			for _, file := range test.files {
				arguments = append(arguments, "--file", file)
			}
			err := ExecuteForTest(root, io.Discard, arguments...)
			if got := ExitCode(err); got != test.wantExit {
				t.Fatalf("exit code = %d, want %d", got, test.wantExit)
			}
			var printed bytes.Buffer
			PrintError(root, &printed, err)
			var problem struct {
				Type string `json:"type"`
			}
			if err := json.Unmarshal(printed.Bytes(), &problem); err != nil {
				t.Fatal(err)
			}
			if problem.Type != test.wantType {
				t.Fatalf("type = %q, want %q", problem.Type, test.wantType)
			}
		})
	}
}

func TestAttachmentAPITypeErrorsKeepTheValidationExitCodeAndProblem(t *testing.T) {
	attachmentPath := filepath.Join(t.TempDir(), "evidence.txt")
	if err := os.WriteFile(attachmentPath, []byte("valid local text"), 0o600); err != nil {
		t.Fatal(err)
	}
	const problemBody = `{"type":"/problems/attachment-type","title":"Unsupported attachment type","status":415,"detail":"The server rejected the declared file type."}`
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		writer.Header().Set("Content-Type", "application/problem+json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		writer.WriteHeader(http.StatusUnsupportedMediaType)
		_, _ = io.WriteString(writer, problemBody)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")
	root := New("1.2.3")
	var output bytes.Buffer
	err := ExecuteForTest(root, &output, "--json", "ticket", "reply", "HLP-42", "--version", "7",
		"--status", "resolved", "--message", "Done", "--file", attachmentPath)
	if got, want := ExitCode(err), 4; got != want {
		t.Fatalf("exit code = %d, want %d", got, want)
	}
	var printed bytes.Buffer
	PrintError(root, &printed, err)
	if got, want := strings.TrimSpace(printed.String()), problemBody; got != want {
		t.Fatalf("problem = %q, want %q", got, want)
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

func TestTicketSnoozeUsesUtcVersionAndIdempotency(t *testing.T) {
	var received map[string]any
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/HLP-42/snooze"; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		if request.Method != http.MethodPut {
			t.Errorf("method = %q", request.Method)
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
		_, _ = io.WriteString(writer, `{ "summary": { "version": 8, "snoozed_until": "2026-09-17T10:00:00Z" } }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	if err := ExecuteForTest(New("1.2.3"), io.Discard, "ticket", "snooze", "HLP-42", "--version", "7", "--until", "2026-09-17T12:00:00+02:00"); err != nil {
		t.Fatal(err)
	}
	if got, want := received["snoozed_until"], "2026-09-17T10:00:00Z"; got != want {
		t.Fatalf("snoozed_until = %q, want %q", got, want)
	}
}

func TestTicketUnsnoozeSendsNull(t *testing.T) {
	var received map[string]any
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if err := json.NewDecoder(request.Body).Decode(&received); err != nil {
			t.Error(err)
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, `{ "summary": { "version": 9, "snoozed_until": null } }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	if err := ExecuteForTest(New("1.2.3"), io.Discard, "ticket", "unsnooze", "HLP-42", "--version", "8"); err != nil {
		t.Fatal(err)
	}
	if value, ok := received["snoozed_until"]; !ok || value != nil {
		t.Fatalf("snoozed_until = %#v, want explicit null", value)
	}
}

func TestTicketReferenceAddUsesVersionAndIdempotency(t *testing.T) {
	var received map[string]any
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/HLP-42/development-references"; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		if request.Method != http.MethodPost {
			t.Errorf("method = %q", request.Method)
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
		_, _ = io.WriteString(writer, `{ "development_references": [{ "label": "GH-42" }] }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	if err := ExecuteForTest(New("1.2.3"), io.Discard, "ticket", "reference", "add", "HLP-42", "--version", "7",
		"--type", "github", "--url", "https://github.com/example/app/issues/42", "--label", "GH-42"); err != nil {
		t.Fatal(err)
	}
	if got, want := received["type"], "github"; got != want {
		t.Fatalf("type = %q, want %q", got, want)
	}
	if got, want := received["label"], "GH-42"; got != want {
		t.Fatalf("label = %q, want %q", got, want)
	}
}

func TestTicketReferenceRemoveUsesDeleteWithoutBody(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/HLP-42/development-references/018f6b45-9e25-7def-a000-112233445566"; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		if request.Method != http.MethodDelete {
			t.Errorf("method = %q", request.Method)
		}
		if request.ContentLength > 0 {
			t.Errorf("ContentLength = %d, want no body", request.ContentLength)
		}
		if request.Header.Get("Idempotency-Key") == "" {
			t.Error("Idempotency-Key is missing")
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, `{ "development_references": [] }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	if err := ExecuteForTest(New("1.2.3"), io.Discard, "ticket", "reference", "remove", "HLP-42",
		"018f6b45-9e25-7def-a000-112233445566", "--version", "8"); err != nil {
		t.Fatal(err)
	}
}

func TestTicketWaitForwardsScopeAndCursorAndUsesStableTimeoutExit(t *testing.T) {
	const timeoutBody = `{"work":null,"cursor":"next-cursor","timed_out":true}`
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/wait"; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		if got, want := request.URL.Query().Get("project_id"), "018f6b45-9e25-7def-a000-112233445566"; got != want {
			t.Errorf("project_id = %q, want %q", got, want)
		}
		if got, want := request.URL.Query().Get("cursor"), "opaque+/cursor"; got != want {
			t.Errorf("cursor = %q, want %q", got, want)
		}
		if got, want := request.URL.Query().Get("timeout_seconds"), "3"; got != want {
			t.Errorf("timeout_seconds = %q, want %q", got, want)
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, timeoutBody)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	root := New("1.2.3")
	var output bytes.Buffer
	err := ExecuteForTest(root, &output, "--json", "ticket", "wait", "--project", "018f6b45-9e25-7def-a000-112233445566",
		"--timeout", "3", "--cursor", "opaque+/cursor")
	if got, want := ExitCode(err), 11; got != want {
		t.Fatalf("exit code = %d, want %d", got, want)
	}
	if output.Len() != 0 {
		t.Fatalf("stdout = %q, want empty", output.String())
	}
	var printed bytes.Buffer
	PrintError(root, &printed, err)
	if got, want := printed.String(), timeoutBody+"\n"; got != want {
		t.Fatalf("error output = %q, want %q", got, want)
	}
}

func TestTicketRequesterTicketsForwardsSameProjectHistoryFilters(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/tickets/HLP-42/requester-tickets"; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		if got, want := request.URL.Query().Get("status"), "resolved"; got != want {
			t.Errorf("status = %q, want %q", got, want)
		}
		if got, want := request.URL.Query().Get("limit"), "10"; got != want {
			t.Errorf("limit = %q, want %q", got, want)
		}
		if got, want := request.URL.Query().Get("cursor"), "opaque+/cursor"; got != want {
			t.Errorf("cursor = %q, want %q", got, want)
		}
		writer.Header().Set("Content-Type", "application/json")
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		_, _ = io.WriteString(writer, `{ "items": [], "next_cursor": null }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	if err := ExecuteForTest(New("1.2.3"), io.Discard, "ticket", "requester-tickets", "HLP-42",
		"--status", "resolved", "--limit", "10", "--cursor", "opaque+/cursor"); err != nil {
		t.Fatal(err)
	}
}

func TestSolutionUpdateUsesProjectScopeVersionIdempotencyAndStdin(t *testing.T) {
	var received map[string]any
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if got, want := request.URL.Path, "/api/backoffice/projects/project-1/solutions/postgres-restart"; got != want {
			t.Errorf("path = %q, want %q", got, want)
		}
		if request.Method != http.MethodPut {
			t.Errorf("method = %q", request.Method)
		}
		if got, want := request.Header.Get("If-Match"), `"3"`; got != want {
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
		_, _ = io.WriteString(writer, `{ "key": "postgres-restart", "version": 4 }`)
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	root := New("1.2.3")
	root.SetIn(strings.NewReader("First line.\r\n\r\nSecond line.\r\n"))
	var output bytes.Buffer
	if err := ExecuteForTest(root, &output, "--json", "solution", "update", "project-1", "postgres-restart",
		"--version", "3", "--title", "Restart PostgreSQL", "--markdown-file", "-"); err != nil {
		t.Fatal(err)
	}
	if got, want := received["title"], "Restart PostgreSQL"; got != want {
		t.Fatalf("title = %q, want %q", got, want)
	}
	if got, want := received["markdown"], "First line.\n\nSecond line.\n"; got != want {
		t.Fatalf("markdown = %q, want %q", got, want)
	}
	if got, want := output.String(), `{"key":"postgres-restart","version":4}`+"\n"; got != want {
		t.Fatalf("output = %q, want %q", got, want)
	}
}

func TestSolutionListForwardsSearchPaginationAndDeleteWritesResult(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		writer.Header().Set("Helpaffe-Version", "1.2.3")
		switch request.Method {
		case http.MethodGet:
			if got, want := request.URL.Path, "/api/backoffice/projects/project-1/solutions"; got != want {
				t.Errorf("path = %q, want %q", got, want)
			}
			if got, want := request.URL.Query().Get("search"), "database restart"; got != want {
				t.Errorf("search = %q, want %q", got, want)
			}
			if got, want := request.URL.Query().Get("limit"), "10"; got != want {
				t.Errorf("limit = %q, want %q", got, want)
			}
			if got, want := request.URL.Query().Get("cursor"), "opaque+/cursor"; got != want {
				t.Errorf("cursor = %q, want %q", got, want)
			}
			writer.Header().Set("Content-Type", "application/json")
			_, _ = io.WriteString(writer, `{"items":[],"next_cursor":null}`)
		case http.MethodDelete:
			if got, want := request.URL.Path, "/api/backoffice/projects/project-1/solutions/postgres-restart"; got != want {
				t.Errorf("path = %q, want %q", got, want)
			}
			if got, want := request.Header.Get("If-Match"), `"4"`; got != want {
				t.Errorf("If-Match = %q, want %q", got, want)
			}
			if request.Header.Get("Idempotency-Key") == "" {
				t.Error("Idempotency-Key is missing")
			}
			writer.WriteHeader(http.StatusNoContent)
		default:
			t.Errorf("unexpected method %q", request.Method)
		}
	}))
	t.Cleanup(server.Close)
	t.Setenv("HELPAFFE_URL", server.URL)
	t.Setenv("HELPAFFE_TOKEN", "hfa_test-token")

	if err := ExecuteForTest(New("1.2.3"), io.Discard, "solution", "list", "project-1",
		"--search", "database restart", "--limit", "10", "--cursor", "opaque+/cursor"); err != nil {
		t.Fatal(err)
	}
	var output bytes.Buffer
	if err := ExecuteForTest(New("1.2.3"), &output, "--json", "solution", "delete", "project-1",
		"postgres-restart", "--version", "4"); err != nil {
		t.Fatal(err)
	}
	if got, want := output.String(), `{"deleted":true,"key":"postgres-restart"}`+"\n"; got != want {
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
