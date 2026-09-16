# Product integration example

This executable ASP.NET Core backend shows the trust boundary around
`Helpaffe.Sdk`. The browser calls this product's `/support` routes. Only the
product backend constructs `HelpaffeProductClient`, reads the `hfp_` key, and
calls helpaffe.

For a local run:

```sh
Helpaffe__ProductApiKey=hfp_your-local-key \
dotnet run --project examples/Helpaffe.ProductExample
```

`X-Example-User-Id` is a visible, intentionally minimal placeholder for the
product's own login/session. Replace `AuthenticatedUser` with real server-side
authentication. The returned id must be stable for that product user; no
helpaffe end-user account is created.

```sh
curl -X POST http://localhost:5000/support/tickets \
  -H 'Content-Type: application/json' \
  -H 'X-Example-User-Id: customer-7' \
  -d '{"subject":"Settings are blank","message":"I cannot open settings.","requestId":"01J-example-create","productVersion":"2.4.1","page":"/settings"}'
```

The example forwards `requestId` as the Product API idempotency key. Replies
also forward the last version shown to the browser, so a concurrent support or
customer update produces a recoverable `stale` response instead of silently
overwriting work. The Product API key is never returned by an endpoint or sent
to the browser.
