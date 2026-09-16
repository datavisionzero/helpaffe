using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        string? idempotencyKey,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseAddress, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _productApiKey);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey.Trim());
        if (expectedVersion is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await HelpaffeApiException.FromResponseAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result ?? throw new HttpRequestException("helpaffe returned an empty or invalid JSON response.");
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
}
