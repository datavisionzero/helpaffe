using System.Net;
using System.Text;
using System.Text.Json;
using Helpaffe.Sdk;

namespace Helpaffe.Sdk.Tests;

public sealed class HelpaffeProductClientTests
{
    [Fact]
    public async Task Client_maps_the_complete_product_ticket_contract()
    {
        var requests = new List<ObservedRequest>();
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add(new ObservedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("If-Match", out var versions) ? versions.Single() : null,
                body));
            return requests.Count switch
            {
                1 => Json(HttpStatusCode.Created, TicketJson("HLP-ONE", "open", 1, "{\"version\":\"2.4.1\"}")),
                2 => Json(HttpStatusCode.OK, $$"""{"items":[{{SummaryJson("HLP-ONE", "open", 1)}}],"next_cursor":null}"""),
                3 => Json(HttpStatusCode.OK, TicketJson("HLP-ONE", "resolved", 3, "{\"version\":\"2.4.1\"}")),
                4 => Json(HttpStatusCode.OK, TicketJson("HLP-ONE", "open", 4, "{\"version\":\"2.4.1\"}")),
                _ => throw new InvalidOperationException("Unexpected request."),
            };
        }));
        var client = new HelpaffeProductClient(httpClient, new Uri("https://helpaffe.example.test"), "hfp_test-secret");
        var context = JsonSerializer.SerializeToElement(new { version = "2.4.1" });

        var created = await client.CreateTicketAsync(
            new CreateProductTicket("customer/7", "Ada", "ada@example.test", "Blank settings", "Please help", context),
            "create-7",
            TestContext.Current.CancellationToken);
        var page = await client.ListTicketsAsync("customer/7", cancellationToken: TestContext.Current.CancellationToken);
        var read = await client.GetTicketAsync("HLP-ONE", "customer/7", TestContext.Current.CancellationToken);
        var replied = await client.AddReplyAsync(
            "HLP-ONE",
            "customer/7",
            "It still fails.",
            read.Summary.Version,
            "reply-7",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProductTicketStatus.Open, created.Summary.Status);
        Assert.Equal("2.4.1", created.Context?.GetProperty("version").GetString());
        Assert.Single(page.Items);
        Assert.Equal(ProductTicketStatus.Resolved, read.Summary.Status);
        Assert.Equal(4, replied.Summary.Version);
        Assert.All(requests, request => Assert.StartsWith("/api/product/", request.Uri.AbsolutePath, StringComparison.Ordinal));
        Assert.All(requests, request => Assert.Equal("Bearer hfp_test-secret", request.Authorization));
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal("create-7", requests[0].IdempotencyKey);
        Assert.Contains("\"external_user_id\":\"customer/7\"", requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("customer%2F7", QueryValue(requests[1].Uri, "external_user_id"));
        Assert.Equal("customer%2F7", QueryValue(requests[2].Uri, "external_user_id"));
        Assert.Equal("reply-7", requests[3].IdempotencyKey);
        Assert.Equal("\"3\"", requests[3].IfMatch);
    }

    [Fact]
    public async Task Problem_details_and_current_version_are_exposed()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(Json(
            HttpStatusCode.PreconditionFailed,
            """{"type":"/problems/stale","title":"The ticket changed","status":412,"detail":"Read again.","current_version":9}"""))));
        var client = new HelpaffeProductClient(httpClient, new Uri("https://helpaffe.example.test"), "hfp_test-secret");

        var exception = await Assert.ThrowsAsync<HelpaffeApiException>(() => client.AddReplyAsync(
            "HLP-ONE",
            "customer-7",
            "Again",
            3,
            "reply-stale",
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.PreconditionFailed, exception.StatusCode);
        Assert.Equal("stale", exception.Code);
        Assert.Equal(9, exception.CurrentVersion);
        Assert.Equal("Read again.", exception.Detail);
    }

    [Fact]
    public async Task Cancellation_reaches_the_HTTP_operation()
    {
        using var httpClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }));
        var client = new HelpaffeProductClient(httpClient, new Uri("http://localhost:8080"), "hfp_test-secret");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListTicketsAsync("customer-7", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Client_rejects_remote_plain_HTTP_and_non_product_credentials()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("No request expected.")));

        Assert.Throws<ArgumentException>(() =>
            new HelpaffeProductClient(httpClient, new Uri("http://helpaffe.example.test"), "hfp_test-secret"));
        Assert.Throws<ArgumentException>(() =>
            new HelpaffeProductClient(httpClient, new Uri("https://helpaffe.example.test"), "hfa_agent-secret"));
    }

    private static string QueryValue(Uri uri, string name) => uri.Query.TrimStart('&', '?').Split('&')
        .Single(value => value.StartsWith(name + "=", StringComparison.Ordinal))
        .Split('=', 2)[1];

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string TicketJson(string number, string status, int version, string context) => $$"""
        {
          "summary": {{SummaryJson(number, status, version)}},
          "requester": { "external_user_id": "customer/7", "name": "Ada", "email": "ada@example.test" },
          "context": {{context}},
          "conversation": [{
            "id": "00000000-0000-0000-0000-000000000002",
            "sequence": 1,
            "kind": "customer_message",
            "body": "Please help",
            "created_at": "2026-09-16T12:00:00Z"
          }]
        }
        """;

    private static string SummaryJson(string number, string status, int version) => $$"""
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "number": "{{number}}",
          "subject": "Blank settings",
          "status": "{{status}}",
          "version": {{version}},
          "created_at": "2026-09-16T12:00:00Z",
          "updated_at": "2026-09-16T12:00:00Z",
          "last_customer_reply_at": "2026-09-16T12:00:00Z"
        }
        """;

    private sealed record ObservedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? IdempotencyKey,
        string? IfMatch,
        string? Body);

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
