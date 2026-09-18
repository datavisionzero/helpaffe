using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helpaffe.Sdk;

/// <summary>A server-side client for the project-bound helpaffe Product API.</summary>
public sealed class HelpaffeProductClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly string _productApiKey;

    /// <summary>Creates a client without taking ownership of <paramref name="httpClient"/>.</summary>
    public HelpaffeProductClient(
        HttpClient httpClient,
        Uri helpaffeAddress,
        string productApiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(helpaffeAddress);
        if (!helpaffeAddress.IsAbsoluteUri)
            throw new ArgumentException("The helpaffe address must be absolute.", nameof(helpaffeAddress));
        if (helpaffeAddress.Scheme is not ("https" or "http"))
            throw new ArgumentException("The helpaffe address must use HTTP or HTTPS.", nameof(helpaffeAddress));
        if (helpaffeAddress.Scheme == "http" && !helpaffeAddress.IsLoopback)
            throw new ArgumentException("Plain HTTP is allowed only for a loopback helpaffe address.", nameof(helpaffeAddress));
        if (string.IsNullOrWhiteSpace(productApiKey) || !productApiKey.StartsWith("hfp_", StringComparison.Ordinal))
            throw new ArgumentException("A product API key beginning with hfp_ is required.", nameof(productApiKey));

        _httpClient = httpClient;
        _baseAddress = new Uri(helpaffeAddress.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _productApiKey = productApiKey;
    }

    /// <summary>Creates a ticket for one product-authenticated end user.</summary>
    public Task<ProductTicket> CreateTicketAsync(
        CreateProductTicket request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<ProductTicket>(
            HttpMethod.Post,
            "api/product/tickets",
            request,
            idempotencyKey,
            expectedVersion: null,
            cancellationToken);
    }

    /// <summary>Creates a ticket and streams up to five attachments without buffering them in memory.</summary>
    public Task<ProductTicket> CreateTicketAsync(
        CreateProductTicket request,
        string idempotencyKey,
        IReadOnlyCollection<ProductAttachmentUpload> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdempotencyKey(idempotencyKey);
        ValidateAttachments(attachments);
        return SendAsync<ProductTicket>(
            HttpMethod.Post,
            "api/product/tickets",
            CreateTicketMultipart(request, attachments),
            idempotencyKey,
            expectedVersion: null,
            cancellationToken);
    }

    /// <summary>Lists tickets belonging to one stable external user id in this key's project.</summary>
    public Task<ProductTicketPage> ListTicketsAsync(
        string externalUserId,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        Required(externalUserId, nameof(externalUserId));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
        var path = $"api/product/tickets?external_user_id={Uri.EscapeDataString(externalUserId.Trim())}&limit={limit}";
        if (!string.IsNullOrWhiteSpace(cursor)) path += $"&cursor={Uri.EscapeDataString(cursor)}";
        return SendAsync<ProductTicketPage>(HttpMethod.Get, path, null, null, null, cancellationToken);
    }

    /// <summary>Reads an end-user-safe ticket with only its public conversation.</summary>
    public Task<ProductTicket> GetTicketAsync(
        string number,
        string externalUserId,
        CancellationToken cancellationToken = default)
    {
        Required(number, nameof(number));
        Required(externalUserId, nameof(externalUserId));
        var path = $"api/product/tickets/{Uri.EscapeDataString(number.Trim())}?external_user_id={Uri.EscapeDataString(externalUserId.Trim())}";
        return SendAsync<ProductTicket>(HttpMethod.Get, path, null, null, null, cancellationToken);
    }

    /// <summary>Adds a customer message using the version from the last ticket read.</summary>
    public Task<ProductTicket> AddReplyAsync(
        string number,
        string externalUserId,
        string message,
        int expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        Required(number, nameof(number));
        Required(externalUserId, nameof(externalUserId));
        Required(message, nameof(message));
        if (expectedVersion < 1) throw new ArgumentOutOfRangeException(nameof(expectedVersion), "The expected version must be positive.");
        ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<ProductTicket>(
            HttpMethod.Post,
            $"api/product/tickets/{Uri.EscapeDataString(number.Trim())}/replies",
            new CustomerReply(externalUserId, message),
            idempotencyKey,
            expectedVersion,
            cancellationToken);
    }

    /// <summary>Adds a customer message and streams up to five attachments without buffering them in memory.</summary>
    public Task<ProductTicket> AddReplyAsync(
        string number,
        string externalUserId,
        string message,
        int expectedVersion,
        string idempotencyKey,
        IReadOnlyCollection<ProductAttachmentUpload> attachments,
        CancellationToken cancellationToken = default)
    {
        Required(number, nameof(number));
        Required(externalUserId, nameof(externalUserId));
        Required(message, nameof(message));
        if (expectedVersion < 1) throw new ArgumentOutOfRangeException(nameof(expectedVersion), "The expected version must be positive.");
        ValidateIdempotencyKey(idempotencyKey);
        ValidateAttachments(attachments);
        var content = new MultipartFormDataContent();
        content.Add(Text(externalUserId), "external_user_id");
        content.Add(Text(message), "message");
        AddAttachments(content, attachments);
        return SendAsync<ProductTicket>(
            HttpMethod.Post,
            $"api/product/tickets/{Uri.EscapeDataString(number.Trim())}/replies",
            content,
            idempotencyKey,
            expectedVersion,
            cancellationToken);
    }

    /// <summary>Streams one public attachment in the authenticated product user's ticket scope.</summary>
    public async Task<ProductAttachmentDownload> DownloadAttachmentAsync(
        string number,
        string externalUserId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        Required(number, nameof(number));
        Required(externalUserId, nameof(externalUserId));
        if (attachmentId == Guid.Empty) throw new ArgumentException("An attachment id is required.", nameof(attachmentId));
        var path = $"api/product/tickets/{Uri.EscapeDataString(number.Trim())}/attachments/{attachmentId}" +
            $"?external_user_id={Uri.EscapeDataString(externalUserId.Trim())}";
        var response = await SendResponseAsync(HttpMethod.Get, path, null, null, null, cancellationToken);
        try
        {
            if (!response.IsSuccessStatusCode) throw await HelpaffeApiException.FromResponseAsync(response, cancellationToken);
            var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var disposition = response.Content.Headers.ContentDisposition;
            var fileName = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"') ?? attachmentId.ToString();
            return new ProductAttachmentDownload(
                response,
                content,
                fileName,
                response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                response.Content.Headers.ContentLength);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        string? idempotencyKey,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        var content = body switch
        {
            null => null,
            HttpContent httpContent => httpContent,
            _ => JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendResponseAsync(
            method, relativePath, content, idempotencyKey, expectedVersion, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await HelpaffeApiException.FromResponseAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result ?? throw new HttpRequestException("helpaffe returned an empty or invalid JSON response.");
    }

    private async Task<HttpResponseMessage> SendResponseAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        string? idempotencyKey,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseAddress, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _productApiKey);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey.Trim());
        if (expectedVersion is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        request.Content = content;

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static MultipartFormDataContent CreateTicketMultipart(
        CreateProductTicket request,
        IReadOnlyCollection<ProductAttachmentUpload> attachments)
    {
        var content = new MultipartFormDataContent();
        content.Add(Text(request.ExternalUserId), "external_user_id");
        content.Add(Text(request.Name), "name");
        content.Add(Text(request.Email), "email");
        content.Add(Text(request.Subject), "subject");
        content.Add(Text(request.Message), "message");
        if (request.Context is not null)
            content.Add(Text(request.Context.Value.GetRawText()), "context");
        AddAttachments(content, attachments);
        return content;
    }

    private static void AddAttachments(
        MultipartFormDataContent multipart,
        IReadOnlyCollection<ProductAttachmentUpload> attachments)
    {
        foreach (var attachment in attachments)
        {
            var streamContent = new StreamContent(new LeaveOpenStream(attachment.Content));
            streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(attachment.MediaType);
            multipart.Add(streamContent, "files", attachment.FileName);
        }
    }

    private static StringContent Text(string value) => new(value, Encoding.UTF8);

    private static void ValidateAttachments(IReadOnlyCollection<ProductAttachmentUpload> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count > 5) throw new ArgumentOutOfRangeException(nameof(attachments), "A message may contain at most five attachments.");
        foreach (var attachment in attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment);
            if (string.IsNullOrWhiteSpace(attachment.FileName) || attachment.FileName.Length > 255 ||
                attachment.FileName.Contains('/') || attachment.FileName.Contains('\\'))
                throw new ArgumentException("Attachment names must be plain file names with at most 255 characters.", nameof(attachments));
            if (!MediaTypeHeaderValue.TryParse(attachment.MediaType, out _))
                throw new ArgumentException("Each attachment requires a valid media type.", nameof(attachments));
            if (!attachment.Content.CanRead)
                throw new ArgumentException("Each attachment stream must be readable.", nameof(attachments));
        }
    }

    private static void Required(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", parameter);
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 200)
            throw new ArgumentException("The idempotency key must contain 1 to 200 characters.", nameof(idempotencyKey));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    private sealed record CustomerReply(string ExternalUserId, string Message);

    private sealed class LeaveOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
