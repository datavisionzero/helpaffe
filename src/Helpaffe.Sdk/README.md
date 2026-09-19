# Helpaffe.Sdk

Server-side .NET client for a product's helpaffe integration. Keep the `hfp_`
product key in backend configuration; never construct this client in browser or
other untrusted code.

Install the package in a .NET 10 backend:

```sh
dotnet add package Helpaffe.Sdk --version 0.1.0
```

```csharp
var client = new HelpaffeProductClient(
    httpClient,
    new Uri(configuration["Helpaffe:BaseAddress"]!),
    configuration["Helpaffe:ProductApiKey"]!);

var ticket = await client.CreateTicketAsync(
    new CreateProductTicket(
        signedInUser.StableId,
        signedInUser.Name,
        signedInUser.Email,
        "Settings are blank",
        "I cannot open settings.",
        JsonSerializer.SerializeToElement(new { version = "2.4.1", page = "/settings" })),
    requestId,
    cancellationToken);
```

The product authenticates its own user and supplies that user's stable id on
every operation. Reads are scoped by that id and by the project embedded in the
product key. Pass the ticket's latest `Summary.Version` and the same request id
when retrying `AddReplyAsync`. API problems are thrown as
`HelpaffeApiException`; cancellation tokens flow to every HTTP operation.

Uploads use caller-owned readable streams and are sent directly as multipart
content; the SDK does not buffer or close those streams. The server accepts up
to five files, 10 MiB each and 25 MiB combined. Use the overload with an
attachment collection for ticket creation or customer replies:

```csharp
await using var screenshot = File.OpenRead("screenshot.png");
var ticket = await client.CreateTicketAsync(
    new CreateProductTicket(
        signedInUser.StableId,
        signedInUser.Name,
        signedInUser.Email,
        "Settings are blank",
        "I cannot open settings."),
    requestId,
    [new ProductAttachmentUpload("screenshot.png", "image/png", screenshot)],
    cancellationToken);
```

Every public conversation entry exposes attachment metadata. Stream a download
inside the same product and end-user boundary, disposing the result when done:

```csharp
var attachment = ticket.Conversation[0].Attachments[0];
await using var download = await client.DownloadAttachmentAsync(
    ticket.Summary.Number,
    signedInUser.StableId,
    attachment.Id,
    cancellationToken);
await download.Content.CopyToAsync(productResponse.Body, cancellationToken);
```

`HelpaffeApiException.Code` exposes stable server failures such as
`attachment-type`, `attachment-too-large`, and `not-found`.
