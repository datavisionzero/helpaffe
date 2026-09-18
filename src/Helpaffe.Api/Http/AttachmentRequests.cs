using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpaffe.Application.Attachments;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Persistence;

namespace Helpaffe.Api.Http;

internal sealed record AttachmentRequestError(int Status, string Code, string Detail);

internal sealed record ValidatedAttachment(
    IFormFile FormFile,
    string FileName,
    string MediaType,
    long Size,
    string Sha256);

internal sealed record AttachmentRequest<T>(
    T? Model,
    IReadOnlyList<ValidatedAttachment> Files,
    string CanonicalBody,
    AttachmentRequestError? Error);

internal static class AttachmentRequests
{
    private static readonly IReadOnlyDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".json"] = "application/json",
            [".zip"] = "application/zip",
        };

    public static async Task<AttachmentRequest<T>> ReadAsync<T>(
        HttpContext context,
        JsonSerializerOptions jsonOptions,
        Func<IFormCollection, T> fromForm,
        params string[] allowedFormFields)
    {
        try
        {
            T? model;
            IReadOnlyList<ValidatedAttachment> files;
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var allowed = allowedFormFields.ToHashSet(StringComparer.Ordinal);
                var unknownField = form.Keys.FirstOrDefault(value => !allowed.Contains(value));
                if (unknownField is not null)
                    return new(default, [], string.Empty,
                        new(400, "unknown-field", $"Unknown multipart form field: {unknownField}."));
                model = fromForm(form);
                var validation = await ValidateAsync(form.Files, context.RequestAborted);
                if (validation.Error is not null)
                    return new(default, [], string.Empty, validation.Error);
                files = validation.Files;
            }
            else if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) is true)
            {
                model = await JsonSerializer.DeserializeAsync<T>(context.Request.Body, jsonOptions, context.RequestAborted);
                files = [];
            }
            else
            {
                return new(default, [], string.Empty,
                    new(415, "content-type", "Use application/json or multipart/form-data."));
            }

            if (model is null)
                return new(default, [], string.Empty, new(400, "validation", "A request body is required."));
            var serialized = JsonSerializer.Serialize(model, jsonOptions);
            var fingerprint = new StringBuilder(serialized);
            foreach (var file in files)
                fingerprint.Append('\n').Append(file.FileName).Append('|').Append(file.MediaType).Append('|')
                    .Append(file.Size).Append('|').Append(file.Sha256);
            return new(model, files, fingerprint.ToString(), null);
        }
        catch (JsonException)
        {
            return new(default, [], string.Empty, new(400, "validation", "The request body is not valid JSON."));
        }
        catch (InvalidDataException exception)
        {
            return new(default, [], string.Empty, new(400, "validation", exception.Message));
        }
        catch (BadHttpRequestException)
        {
            return new(default, [], string.Empty,
                new(413, "attachment-too-large", $"Attachments may contain at most {TicketAttachment.MaximumTotalSize} bytes in total."));
        }
    }

    public static async Task<IReadOnlyList<string>> StoreAsync(
        Ticket ticket,
        Guid conversationEntryId,
        IReadOnlyList<ValidatedAttachment> files,
        IAttachmentStorage storage,
        HelpaffeDbContext database,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var stored = new List<string>();
        try
        {
            foreach (var file in files)
            {
                var storageKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                await using var content = file.FormFile.OpenReadStream();
                await storage.SaveAsync(storageKey, content, cancellationToken);
                stored.Add(storageKey);
                var attachment = ticket.AddAttachment(
                    Guid.NewGuid(), conversationEntryId, file.FileName, file.MediaType, file.Size, storageKey, createdAt);
                database.TicketAttachments.Add(attachment);
            }
            return stored;
        }
        catch
        {
            await DeleteAsync(storage, stored, CancellationToken.None);
            throw;
        }
    }

    public static async Task DeleteAsync(
        IAttachmentStorage storage,
        IEnumerable<string> storageKeys,
        CancellationToken cancellationToken)
    {
        foreach (var storageKey in storageKeys)
        {
            try
            {
                await storage.DeleteAsync(storageKey, cancellationToken);
            }
            catch (IOException)
            {
                // Database consistency takes precedence; a failed cleanup leaves only an unreachable file.
            }
        }
    }

    private static async Task<(IReadOnlyList<ValidatedAttachment> Files, AttachmentRequestError? Error)> ValidateAsync(
        IFormFileCollection files,
        CancellationToken cancellationToken)
    {
        if (files.Count > TicketAttachment.MaximumFilesPerMessage)
            return ([], new(400, "attachment-limit", $"A message may contain at most {TicketAttachment.MaximumFilesPerMessage} attachments."));
        if (files.Any(value => !string.Equals(value.Name, "files", StringComparison.Ordinal)))
            return ([], new(400, "validation", "Attachment form fields must be named files."));
        if (files.Sum(value => value.Length) > TicketAttachment.MaximumTotalSize)
            return ([], new(413, "attachment-too-large", $"Attachments may contain at most {TicketAttachment.MaximumTotalSize} bytes in total."));

        var validated = new List<ValidatedAttachment>(files.Count);
        foreach (var file in files)
        {
            if (file.Length is < 1 or > TicketAttachment.MaximumFileSize)
                return ([], new(413, "attachment-too-large", $"Each attachment must contain between 1 and {TicketAttachment.MaximumFileSize} bytes."));
            var fileName = file.FileName.Normalize(NormalizationForm.FormC);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 ||
                fileName.Contains('/') || fileName.Contains('\\') || fileName.Any(char.IsControl))
                return ([], new(400, "attachment-name", "Attachment names must be plain file names with at most 255 characters."));
            var extension = Path.GetExtension(fileName);
            if (!MediaTypes.TryGetValue(extension, out var mediaType))
                return ([], new(415, "attachment-type", "Allowed attachment types are PDF, PNG, JPEG, GIF, WebP, TXT, CSV, JSON, and ZIP."));
            if (!string.IsNullOrWhiteSpace(file.ContentType) &&
                !string.Equals(file.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(file.ContentType.Split(';', 2)[0].Trim(), mediaType, StringComparison.OrdinalIgnoreCase))
                return ([], new(415, "attachment-type", $"The declared media type does not match {extension}."));

            await using var content = file.OpenReadStream();
            if (!await ContentMatchesAsync(content, extension, cancellationToken))
                return ([], new(415, "attachment-type", $"The content does not match {extension}."));
            content.Position = 0;
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
            validated.Add(new(file, fileName, mediaType, file.Length, digest));
        }
        return (validated, null);
    }

    private static async Task<bool> ContentMatchesAsync(Stream content, string extension, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var read = await content.ReadAsync(header, cancellationToken);
        var prefix = header.AsSpan(0, read);
        switch (extension.ToLowerInvariant())
        {
            case ".pdf": return prefix.StartsWith("%PDF-"u8);
            case ".png": return prefix.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
            case ".jpg":
            case ".jpeg": return prefix.StartsWith(new byte[] { 0xff, 0xd8, 0xff });
            case ".gif": return prefix.StartsWith("GIF87a"u8) || prefix.StartsWith("GIF89a"u8);
            case ".webp": return read >= 12 && prefix[..4].SequenceEqual("RIFF"u8) && prefix[8..12].SequenceEqual("WEBP"u8);
            case ".zip": return prefix.StartsWith(new byte[] { 0x50, 0x4b, 0x03, 0x04 }) ||
                prefix.StartsWith(new byte[] { 0x50, 0x4b, 0x05, 0x06 });
            case ".json":
                content.Position = 0;
                try
                {
                    using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
                    return document.RootElement.ValueKind is not JsonValueKind.Undefined;
                }
                catch (JsonException)
                {
                    return false;
                }
            case ".txt":
            case ".csv":
                content.Position = 0;
                return await IsUtf8TextAsync(content, cancellationToken);
            default: return false;
        }
    }

    private static async Task<bool> IsUtf8TextAsync(Stream content, CancellationToken cancellationToken)
    {
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192];
        try
        {
            int read;
            while ((read = await content.ReadAsync(bytes, cancellationToken)) > 0)
                decoder.Convert(bytes, 0, read, chars, 0, chars.Length, false, out _, out _, out _);
            decoder.Convert([], 0, 0, chars, 0, chars.Length, true, out _, out _, out _);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
