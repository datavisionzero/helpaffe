package cmd

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"mime"
	"mime/multipart"
	"net/http"
	"net/textproto"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"unicode"
	"unicode/utf8"

	"github.com/spf13/cobra"
)

const (
	maximumAttachmentFiles    = 5
	maximumAttachmentFileSize = 10 * 1024 * 1024
	maximumAttachmentTotal    = 25 * 1024 * 1024
)

var attachmentMediaTypes = map[string]string{
	".pdf":  "application/pdf",
	".png":  "image/png",
	".jpg":  "image/jpeg",
	".jpeg": "image/jpeg",
	".gif":  "image/gif",
	".webp": "image/webp",
	".txt":  "text/plain",
	".csv":  "text/csv",
	".json": "application/json",
	".zip":  "application/zip",
}

type localProblemError struct {
	code     string
	exitCode int
	message  string
}

func (err *localProblemError) Error() string { return err.message }
func (err *localProblemError) Unwrap() error {
	return &exitError{code: err.exitCode, message: err.message}
}

type attachmentUpload struct {
	file      *os.File
	name      string
	mediaType string
	size      int64
}

func closeAttachments(attachments []*attachmentUpload) {
	for _, attachment := range attachments {
		_ = attachment.file.Close()
	}
}

func prepareAttachments(paths []string) ([]*attachmentUpload, error) {
	if len(paths) > maximumAttachmentFiles {
		return nil, &localProblemError{
			code: "attachment-limit", exitCode: 4,
			message: fmt.Sprintf("a message may contain at most %d attachments", maximumAttachmentFiles),
		}
	}
	attachments := make([]*attachmentUpload, 0, len(paths))
	var total int64
	for _, path := range paths {
		file, err := os.Open(path)
		if err != nil {
			closeAttachments(attachments)
			return nil, &localProblemError{code: "attachment-file", exitCode: 2, message: fmt.Sprintf("cannot open attachment %q: %v", path, err)}
		}
		info, err := file.Stat()
		if err != nil || !info.Mode().IsRegular() {
			_ = file.Close()
			closeAttachments(attachments)
			if err != nil {
				return nil, &localProblemError{code: "attachment-file", exitCode: 2, message: fmt.Sprintf("cannot inspect attachment %q: %v", path, err)}
			}
			return nil, &localProblemError{code: "attachment-file", exitCode: 2, message: fmt.Sprintf("attachment %q is not a regular file", path)}
		}
		name := filepath.Base(path)
		if name == "." || name == string(filepath.Separator) || strings.ContainsAny(name, `/\`) ||
			utf8.RuneCountInString(name) > 255 || strings.IndexFunc(name, unicode.IsControl) >= 0 {
			_ = file.Close()
			closeAttachments(attachments)
			return nil, &localProblemError{code: "attachment-file", exitCode: 2, message: fmt.Sprintf("attachment %q does not have a safe file name", path)}
		}
		if info.Size() < 1 || info.Size() > maximumAttachmentFileSize {
			_ = file.Close()
			closeAttachments(attachments)
			return nil, &localProblemError{
				code: "attachment-too-large", exitCode: 4,
				message: fmt.Sprintf("attachment %q must contain between 1 and %d bytes", path, maximumAttachmentFileSize),
			}
		}
		total += info.Size()
		if total > maximumAttachmentTotal {
			_ = file.Close()
			closeAttachments(attachments)
			return nil, &localProblemError{
				code: "attachment-too-large", exitCode: 4,
				message: fmt.Sprintf("attachments may contain at most %d bytes in total", maximumAttachmentTotal),
			}
		}
		extension := strings.ToLower(filepath.Ext(name))
		mediaType, ok := attachmentMediaTypes[extension]
		if !ok || !attachmentContentMatches(file, extension) {
			_ = file.Close()
			closeAttachments(attachments)
			return nil, &localProblemError{
				code: "attachment-type", exitCode: 4,
				message: fmt.Sprintf("attachment %q is not a valid PDF, PNG, JPEG, GIF, WebP, TXT, CSV, JSON, or ZIP file", path),
			}
		}
		attachments = append(attachments, &attachmentUpload{file: file, name: name, mediaType: mediaType, size: info.Size()})
	}
	return attachments, nil
}

func attachmentContentMatches(file *os.File, extension string) bool {
	defer func() { _, _ = file.Seek(0, io.SeekStart) }()
	header := make([]byte, 12)
	read, err := io.ReadFull(file, header)
	if err != nil && err != io.ErrUnexpectedEOF {
		return false
	}
	header = header[:read]
	switch extension {
	case ".pdf":
		return bytes.HasPrefix(header, []byte("%PDF-"))
	case ".png":
		return bytes.HasPrefix(header, []byte{0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a})
	case ".jpg", ".jpeg":
		return bytes.HasPrefix(header, []byte{0xff, 0xd8, 0xff})
	case ".gif":
		return bytes.HasPrefix(header, []byte("GIF87a")) || bytes.HasPrefix(header, []byte("GIF89a"))
	case ".webp":
		return len(header) >= 12 && bytes.Equal(header[:4], []byte("RIFF")) && bytes.Equal(header[8:12], []byte("WEBP"))
	case ".zip":
		return bytes.HasPrefix(header, []byte{0x50, 0x4b, 0x03, 0x04}) || bytes.HasPrefix(header, []byte{0x50, 0x4b, 0x05, 0x06})
	case ".json":
		if _, err := file.Seek(0, io.SeekStart); err != nil {
			return false
		}
		decoder := json.NewDecoder(file)
		var value any
		if err := decoder.Decode(&value); err != nil {
			return false
		}
		return decoder.Decode(&struct{}{}) == io.EOF
	case ".txt", ".csv":
		if _, err := file.Seek(0, io.SeekStart); err != nil {
			return false
		}
		content, err := io.ReadAll(io.LimitReader(file, maximumAttachmentFileSize+1))
		return err == nil && utf8.Valid(content)
	default:
		return false
	}
}

type multipartField struct {
	name  string
	value string
}

func (client *client) requestMultipart(
	method, path string,
	fields []multipartField,
	attachments []*attachmentUpload,
	version int,
	idempotencyKey string,
) ([]byte, http.Header, error) {
	reader, pipeWriter := io.Pipe()
	writer := multipart.NewWriter(pipeWriter)
	request, err := client.newRequest(method, path, reader, version, idempotencyKey)
	if err != nil {
		_ = reader.Close()
		_ = pipeWriter.Close()
		return nil, nil, &exitError{code: 2, message: err.Error()}
	}
	request.Header.Set("Content-Type", writer.FormDataContentType())
	go writeMultipart(pipeWriter, writer, fields, attachments)
	response, err := client.http.Do(request)
	_ = reader.Close()
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

func writeMultipart(
	pipeWriter *io.PipeWriter,
	writer *multipart.Writer,
	fields []multipartField,
	attachments []*attachmentUpload,
) {
	for _, field := range fields {
		if err := writer.WriteField(field.name, field.value); err != nil {
			_ = pipeWriter.CloseWithError(err)
			return
		}
	}
	for _, attachment := range attachments {
		disposition := mime.FormatMediaType("form-data", map[string]string{"name": "files", "filename": attachment.name})
		part, err := writer.CreatePart(textproto.MIMEHeader{
			"Content-Disposition": {disposition},
			"Content-Type":        {attachment.mediaType},
		})
		if err != nil {
			_ = pipeWriter.CloseWithError(err)
			return
		}
		if _, err := io.Copy(part, attachment.file); err != nil {
			_ = pipeWriter.CloseWithError(err)
			return
		}
	}
	if err := writer.Close(); err != nil {
		_ = pipeWriter.CloseWithError(err)
		return
	}
	_ = pipeWriter.Close()
}

func (client *client) downloadAttachment(path string) (*http.Response, error) {
	request, err := client.newRequest(http.MethodGet, path, nil, 0, "")
	if err != nil {
		return nil, &exitError{code: 2, message: err.Error()}
	}
	response, err := client.http.Do(request)
	if err != nil {
		return nil, &exitError{code: 10, message: err.Error()}
	}
	if err := client.validateServerVersion(response.Header); err != nil {
		_ = response.Body.Close()
		return nil, err
	}
	if response.StatusCode >= 400 {
		defer response.Body.Close()
		body, readErr := io.ReadAll(io.LimitReader(response.Body, 10<<20))
		if readErr != nil {
			return nil, &exitError{code: 10, message: readErr.Error()}
		}
		return nil, parseAPIError(response.StatusCode, body)
	}
	return response, nil
}

func (app *application) newTicketAttachmentCommand() *cobra.Command {
	attachment := &cobra.Command{Use: "attachment", Short: "Download ticket attachments"}
	attachment.AddCommand(app.newTicketAttachmentDownloadCommand())
	return attachment
}

func (app *application) newTicketAttachmentDownloadCommand() *cobra.Command {
	var output string
	command := &cobra.Command{
		Use:   "download NUMBER ATTACHMENT_ID",
		Short: "Download one visible attachment without overwriting a local file",
		Args:  cobra.ExactArgs(2),
		RunE: func(command *cobra.Command, args []string) error {
			if strings.TrimSpace(output) == "" {
				return &exitError{code: 2, message: "--output is required"}
			}
			if _, err := os.Lstat(output); err == nil {
				return &localProblemError{code: "attachment-output", exitCode: 2, message: fmt.Sprintf("output file %q already exists", output)}
			} else if !os.IsNotExist(err) {
				return &localProblemError{code: "attachment-output", exitCode: 2, message: fmt.Sprintf("cannot inspect output path %q: %v", output, err)}
			}
			client, err := app.apiClient()
			if err != nil {
				return err
			}
			path := "/api/backoffice/tickets/" + url.PathEscape(args[0]) + "/attachments/" + url.PathEscape(args[1])
			response, err := client.downloadAttachment(path)
			if err != nil {
				return err
			}
			defer response.Body.Close()
			target, err := os.OpenFile(output, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
			if err != nil {
				return &localProblemError{code: "attachment-output", exitCode: 2, message: fmt.Sprintf("cannot create output file %q: %v", output, err)}
			}
			written, copyErr := io.Copy(target, response.Body)
			closeErr := target.Close()
			if copyErr != nil || closeErr != nil || (response.ContentLength >= 0 && written != response.ContentLength) {
				_ = os.Remove(output)
				if copyErr == nil {
					copyErr = closeErr
				}
				if copyErr == nil {
					copyErr = io.ErrUnexpectedEOF
				}
				return &localProblemError{code: "attachment-output", exitCode: 10, message: fmt.Sprintf("cannot write attachment %q: %v", output, copyErr)}
			}
			fileName := args[1]
			if disposition := response.Header.Get("Content-Disposition"); disposition != "" {
				if _, parameters, parseErr := mime.ParseMediaType(disposition); parseErr == nil && parameters["filename"] != "" {
					fileName = parameters["filename"]
				}
			}
			result, err := json.Marshal(map[string]any{
				"attachment_id": args[1],
				"file_name":     fileName,
				"media_type":    response.Header.Get("Content-Type"),
				"size":          written,
				"path":          output,
			})
			if err != nil {
				return err
			}
			if app.json {
				return app.write(command, result)
			}
			_, err = fmt.Fprintf(command.OutOrStdout(), "Downloaded %s (%d bytes) to %s\n", fileName, written, output)
			return err
		},
	}
	command.Flags().StringVarP(&output, "output", "o", "", "new local output path")
	return command
}
