# Helpaffe.Sdk

Server-side .NET client for a product's helpaffe integration. Keep the `hfp_`
product key in backend configuration; never construct this client in browser or
other untrusted code.

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
