using System.Text.Json;
using Helpaffe.Sdk;

var builder = WebApplication.CreateBuilder(args);
var helpaffeAddress = builder.Configuration["Helpaffe:BaseAddress"]
    ?? throw new InvalidOperationException("Helpaffe:BaseAddress is required.");
var productApiKey = builder.Configuration["Helpaffe:ProductApiKey"]
    ?? throw new InvalidOperationException("Helpaffe:ProductApiKey is required and must remain on the product server.");

builder.Services.AddHttpClient("helpaffe");
builder.Services.AddSingleton(services => new HelpaffeProductClient(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("helpaffe"),
    new Uri(helpaffeAddress),
    productApiKey));

var app = builder.Build();

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
    CreateTicketInput input,
    HttpContext context,
    HelpaffeProductClient helpaffe,
    CancellationToken cancellationToken) =>
{
    var user = AuthenticatedUser(context);
    if (user is null) return Results.Unauthorized();
    var technicalContext = JsonSerializer.SerializeToElement(new
    {
        product_version = input.ProductVersion,
        page = input.Page,
        user_agent = context.Request.Headers.UserAgent.ToString(),
    });
    var ticket = await helpaffe.CreateTicketAsync(
        new CreateProductTicket(user.Id, user.Name, user.Email, input.Subject, input.Message, technicalContext),
        input.RequestId,
        cancellationToken);
    return Results.Created($"/support/tickets/{ticket.Summary.Number}", ticket);
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
    ReplyInput input,
    HttpContext context,
    HelpaffeProductClient helpaffe,
    CancellationToken cancellationToken) =>
{
    var user = AuthenticatedUser(context);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(await helpaffe.AddReplyAsync(
            number,
            user.Id,
            input.Message,
            input.Version,
            input.RequestId,
            cancellationToken));
});

await app.RunAsync();

internal sealed record ProductUser(string Id, string Name, string Email);
internal sealed record CreateTicketInput(
    string Subject,
    string Message,
    string RequestId,
    string ProductVersion,
    string Page);
internal sealed record ReplyInput(string Message, int Version, string RequestId);
