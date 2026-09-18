using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helpaffe.Sdk;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var helpaffeAddress = builder.Configuration["Helpaffe:BaseAddress"]
    ?? throw new InvalidOperationException("Helpaffe:BaseAddress is required.");
var productApiKey = builder.Configuration["Helpaffe:ProductApiKey"]
    ?? throw new InvalidOperationException("Helpaffe:ProductApiKey is required and must remain on the product server.");

builder.Services.AddHttpClient("helpaffe");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 26 * 1024 * 1024);
builder.Services.AddSingleton(services => new HelpaffeProductClient(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("helpaffe"),
    new Uri(helpaffeAddress),
    productApiKey));

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (exception is HelpaffeApiException apiException)
    {
        context.Response.StatusCode = (int)(apiException.StatusCode ?? System.Net.HttpStatusCode.BadGateway);
        await Results.Problem(
            detail: apiException.Detail,
            statusCode: context.Response.StatusCode,
            title: apiException.Title,
            type: apiException.Type).ExecuteAsync(context);
        return;
    }

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await Results.Problem(
        detail: "The product backend could not complete the support request.",
        statusCode: context.Response.StatusCode,
        title: "Support request failed").ExecuteAsync(context);
}));
app.UseDefaultFiles();
app.UseStaticFiles();

// This header is deliberately only a runnable stand-in. A real product must resolve
// the same stable id, name, and email from its own authenticated server session.
static ProductUser? AuthenticatedUser(HttpContext context)
{
    var externalUserId = context.Request.Headers["X-Example-User-Id"].ToString();
    return string.IsNullOrWhiteSpace(externalUserId)
        ? null
        : new ProductUser(externalUserId, "Example User", $"{externalUserId}@example.test");
}

var support = app.MapGroup("/support");
support.MapPost("/tickets", async (
    HttpContext context,
    HelpaffeProductClient helpaffe,
    CancellationToken cancellationToken) =>
{
    var user = AuthenticatedUser(context);
    if (user is null) return Results.Unauthorized();
    var submitted = await ExampleInput.ReadCreateAsync(context, cancellationToken);
    if (submitted is null) return Results.BadRequest(new { detail = "A JSON or multipart ticket request is required." });
    var input = submitted.Input;
    var technicalContext = JsonSerializer.SerializeToElement(new
    {
        product_version = input.ProductVersion,
        page = input.Page,
        user_agent = context.Request.Headers.UserAgent.ToString(),
    });
    var streams = submitted.Files.Select(file => file.OpenReadStream()).ToArray();
    try
    {
        var attachments = submitted.Files.Select((file, index) => new ProductAttachmentUpload(
            file.FileName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            streams[index])).ToArray();
        var request = new CreateProductTicket(user.Id, user.Name, user.Email, input.Subject, input.Message, technicalContext);
        var ticket = attachments.Length == 0
            ? await helpaffe.CreateTicketAsync(request, input.RequestId, cancellationToken)
            : await helpaffe.CreateTicketAsync(request, input.RequestId, attachments, cancellationToken);
        return Results.Created($"/support/tickets/{ticket.Summary.Number}", ticket);
    }
    finally
    {
        foreach (var stream in streams) await stream.DisposeAsync();
    }
});

support.MapGet("/tickets", async (
    HttpContext context,
    HelpaffeProductClient helpaffe,
    CancellationToken cancellationToken) =>
{
    var user = AuthenticatedUser(context);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(await helpaffe.ListTicketsAsync(user.Id, cancellationToken: cancellationToken));
});

support.MapGet("/tickets/{number}", async (
    string number,
    HttpContext context,
    HelpaffeProductClient helpaffe,
    CancellationToken cancellationToken) =>
{
    var user = AuthenticatedUser(context);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(await helpaffe.GetTicketAsync(number, user.Id, cancellationToken));
});

support.MapPost("/tickets/{number}/replies", async (
    string number,
    HttpContext context,
    HelpaffeProductClient helpaffe,
    CancellationToken cancellationToken) =>
{
    var user = AuthenticatedUser(context);
    if (user is null) return Results.Unauthorized();
    var submitted = await ExampleInput.ReadReplyAsync(context, cancellationToken);
    if (submitted is null) return Results.BadRequest(new { detail = "A JSON or multipart reply request is required." });
    var streams = submitted.Files.Select(file => file.OpenReadStream()).ToArray();
    try
    {
        var attachments = submitted.Files.Select((file, index) => new ProductAttachmentUpload(
            file.FileName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            streams[index])).ToArray();
        var input = submitted.Input;
        var ticket = attachments.Length == 0
            ? await helpaffe.AddReplyAsync(number, user.Id, input.Message, input.Version, input.RequestId, cancellationToken)
            : await helpaffe.AddReplyAsync(number, user.Id, input.Message, input.Version, input.RequestId, attachments, cancellationToken);
        return Results.Ok(ticket);
    }
    finally
    {
        foreach (var stream in streams) await stream.DisposeAsync();
    }
});

support.MapGet("/tickets/{number}/attachments/{attachmentId:guid}", async (
    string number,
    Guid attachmentId,
    HttpContext context,
    HelpaffeProductClient helpaffe,
    CancellationToken cancellationToken) =>
{
    var user = AuthenticatedUser(context);
    if (user is null) return Results.Unauthorized();
    var download = await helpaffe.DownloadAttachmentAsync(number, user.Id, attachmentId, cancellationToken);
    return new AttachmentProxyResult(download);
});

await app.RunAsync();

public sealed class ProductExampleApplication;

internal sealed record ProductUser(string Id, string Name, string Email);
internal sealed record CreateTicketInput(
    string Subject,
    string Message,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("productVersion")] string ProductVersion,
    string Page);
internal sealed record ReplyInput(
    string Message,
    int Version,
    [property: JsonPropertyName("requestId")] string RequestId);
internal sealed record SubmittedInput<T>(T Input, IFormFileCollection Files);

internal static class ExampleInput
{
    public static async Task<SubmittedInput<CreateTicketInput>?> ReadCreateAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            var input = await context.Request.ReadFromJsonAsync<CreateTicketInput>(cancellationToken);
            return input is null ? null : new(input, new FormFileCollection());
        }
        var form = await context.Request.ReadFormAsync(cancellationToken);
        return new(new CreateTicketInput(
            form["subject"].ToString(),
            form["message"].ToString(),
            form["requestId"].ToString(),
            form["productVersion"].ToString(),
            form["page"].ToString()), form.Files);
    }

    public static async Task<SubmittedInput<ReplyInput>?> ReadReplyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            var input = await context.Request.ReadFromJsonAsync<ReplyInput>(cancellationToken);
            return input is null ? null : new(input, new FormFileCollection());
        }
        var form = await context.Request.ReadFormAsync(cancellationToken);
        return int.TryParse(form["version"], out var version)
            ? new(new ReplyInput(form["message"].ToString(), version, form["requestId"].ToString()), form.Files)
            : null;
    }
}

internal sealed class AttachmentProxyResult(ProductAttachmentDownload download) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        await using (download)
        {
            httpContext.Response.ContentType = download.MediaType;
            if (download.Size is not null) httpContext.Response.ContentLength = download.Size;
            httpContext.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = download.FileName,
            }.ToString();
            await download.Content.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
        }
    }
}
