using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Helpaffe.Sdk;

/// <summary>An RFC 9457 problem response returned by helpaffe.</summary>
public sealed class HelpaffeApiException : HttpRequestException
{
    private HelpaffeApiException(
        HttpStatusCode statusCode,
        string? type,
        string? title,
        string? detail,
        int? currentVersion)
        : base(detail ?? title ?? $"helpaffe returned HTTP {(int)statusCode}.", null, statusCode)
    {
        Type = type;
        Title = title;
        Detail = detail;
        CurrentVersion = currentVersion;
    }

    /// <summary>The stable problem type URI.</summary>
    public string? Type { get; }
    /// <summary>The human-readable problem title.</summary>
    public string? Title { get; }
    /// <summary>The human-readable problem explanation.</summary>
    public string? Detail { get; }
    /// <summary>The current ticket version supplied with a stale response.</summary>
    public int? CurrentVersion { get; }
    /// <summary>The machine-readable final segment of <see cref="Type"/>.</summary>
    public string? Code => Type?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    internal static async Task<HelpaffeApiException> FromResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Problem? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<Problem>(cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            // A non-problem response is still represented as a typed HTTP failure below.
        }
        return new HelpaffeApiException(
            response.StatusCode,
            problem?.Type,
            problem?.Title,
            problem?.Detail,
            problem?.CurrentVersion);
    }

    private sealed record Problem(
        string? Type,
        string? Title,
        string? Detail,
        [property: JsonPropertyName("current_version")]
        int? CurrentVersion);
}
