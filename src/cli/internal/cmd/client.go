package cmd

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"runtime"
	"strings"
	"time"
)

type client struct {
	baseURL string
	token   string
	version string
	http    *http.Client
}

type apiError struct {
	code    int
	status  int
	problem string
	detail  string
	body    []byte
}

func (err *apiError) Error() string {
	if err.detail != "" {
		return err.detail
	}
	return fmt.Sprintf("server returned HTTP %d", err.status)
}

func (app *application) apiClient() (*client, error) {
	baseURL := strings.TrimSpace(app.url)
	if baseURL == "" {
		baseURL = strings.TrimSpace(os.Getenv("HELPAFFE_URL"))
	}
	if baseURL == "" {
		return nil, &exitError{code: 2, message: "helpaffe URL is required; use --url or HELPAFFE_URL"}
	}
	parsed, err := url.Parse(baseURL)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		return nil, &exitError{code: 2, message: "helpaffe URL is invalid"}
	}
	allowHTTP := app.insecureHTTP || os.Getenv("HELPAFFE_INSECURE_HTTP") == "1"
	if parsed.Scheme == "http" && !isLoopback(parsed.Hostname()) && !allowHTTP {
		return nil, &exitError{code: 2, message: "plain HTTP is allowed only on loopback; use --insecure-http to override"}
	}
	if parsed.Scheme != "https" && parsed.Scheme != "http" {
		return nil, &exitError{code: 2, message: "helpaffe URL must use https or http"}
	}

	token := strings.TrimSpace(os.Getenv("HELPAFFE_TOKEN"))
	if token == "" && app.tokenFile != "" {
		token, err = readTokenFile(app.tokenFile)
		if err != nil {
			return nil, err
		}
	}
	if token == "" {
		return nil, &exitError{code: 2, message: "agent token is required; use HELPAFFE_TOKEN or --token-file"}
	}
	if !strings.HasPrefix(token, "hfa_") {
		return nil, &exitError{code: 2, message: "the configured credential is not a named agent token"}
	}
	return &client{
		baseURL: strings.TrimRight(baseURL, "/"),
		token:   token,
		version: app.version,
		http:    &http.Client{Timeout: 30 * time.Second},
	}, nil
}

func readTokenFile(path string) (string, error) {
	info, err := os.Stat(path)
	if err != nil {
		return "", &exitError{code: 2, message: fmt.Sprintf("cannot read token file: %v", err)}
	}
	if runtime.GOOS != "windows" && info.Mode().Perm()&0o077 != 0 {
		return "", &exitError{code: 2, message: "token file permissions are too broad; use mode 0600"}
	}
	content, err := os.ReadFile(path)
	if err != nil {
		return "", &exitError{code: 2, message: fmt.Sprintf("cannot read token file: %v", err)}
	}
	return strings.TrimSpace(string(content)), nil
}

func isLoopback(host string) bool {
	if strings.EqualFold(host, "localhost") {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

func (client *client) request(method, path string, payload any, version int, idempotencyKey string) ([]byte, http.Header, error) {
	var body io.Reader
	if payload != nil {
		encoded, err := json.Marshal(payload)
		if err != nil {
			return nil, nil, err
		}
		body = bytes.NewReader(encoded)
	}
	request, err := client.newRequest(method, path, body, version, idempotencyKey)
	if err != nil {
		return nil, nil, &exitError{code: 2, message: err.Error()}
	}
	if payload != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	response, err := client.http.Do(request)
	if err != nil {
		return nil, nil, &exitError{code: 10, message: err.Error()}
	}
	defer response.Body.Close()
	responseBody, err := io.ReadAll(io.LimitReader(response.Body, 10<<20))
	if err != nil {
		return nil, nil, &exitError{code: 10, message: err.Error()}
	}
	if err := client.validateServerVersion(response.Header); err != nil {
		return nil, nil, err
	}
	if response.StatusCode >= 400 {
		return nil, response.Header, parseAPIError(response.StatusCode, responseBody)
	}
	return responseBody, response.Header, nil
}

func (client *client) newRequest(
	method, path string,
	body io.Reader,
	version int,
	idempotencyKey string,
) (*http.Request, error) {
	request, err := http.NewRequest(method, client.baseURL+path, body)
	if err != nil {
		return nil, err
	}
	request.Header.Set("Authorization", "Bearer "+client.token)
	request.Header.Set("User-Agent", fmt.Sprintf("helpaffe/%s (%s/%s)", client.version, runtime.GOOS, runtime.GOARCH))
	if version > 0 {
		request.Header.Set("If-Match", fmt.Sprintf("\"%d\"", version))
	}
	if idempotencyKey != "" {
		request.Header.Set("Idempotency-Key", idempotencyKey)
	}
	return request, nil
}

func (client *client) validateServerVersion(headers http.Header) error {
	serverVersion := headers.Get("Helpaffe-Version")
	if serverVersion != "" && versionCore(serverVersion) != versionCore(client.version) {
		return &exitError{code: 9, message: fmt.Sprintf(
			"client/server version mismatch: client %s, server %s", client.version, serverVersion)}
	}
	return nil
}

func parseAPIError(status int, body []byte) error {
	var problem struct {
		Type   string `json:"type"`
		Detail string `json:"detail"`
	}
	_ = json.Unmarshal(body, &problem)
	code := 1
	switch {
	case status == http.StatusNotFound && strings.HasSuffix(problem.Type, "/no-ticket"):
		code = 8
	case status == http.StatusNotFound:
		code = 3
	case status == http.StatusBadRequest || status == http.StatusRequestEntityTooLarge ||
		status == http.StatusUnsupportedMediaType || status == http.StatusUnprocessableEntity ||
		status == http.StatusPreconditionRequired:
		code = 4
	case status == http.StatusConflict:
		code = 5
	case status == http.StatusPreconditionFailed:
		code = 6
	case status == http.StatusUnauthorized || status == http.StatusForbidden:
		code = 7
	}
	return &apiError{code: code, status: status, problem: problem.Type, detail: problem.Detail, body: bytes.TrimSpace(body)}
}

func (err *apiError) Unwrap() error { return &exitError{code: err.code, message: err.Error()} }

func versionCore(version string) string {
	return strings.SplitN(version, "-", 2)[0]
}

func requestKey() (string, error) {
	value := make([]byte, 16)
	if _, err := rand.Read(value); err != nil {
		return "", err
	}
	value[6] = value[6]&0x0f | 0x40
	value[8] = value[8]&0x3f | 0x80
	encoded := hex.EncodeToString(value)
	return fmt.Sprintf("%s-%s-%s-%s-%s", encoded[0:8], encoded[8:12], encoded[12:16], encoded[16:20], encoded[20:32]), nil
}
