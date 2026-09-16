# Product integration

A product integrates from its trusted backend. It authenticates its own user,
then sends that user's stable product-local id to helpaffe. The `hfp_` product
key belongs in server-side configuration and must never be included in browser
JavaScript, HTML, or responses.

The [.NET SDK](../src/Helpaffe.Sdk/README.md) wraps the same Product API calls
shown below. Other backend stacks can use the checked-in
[OpenAPI contract](api/product.openapi.json) or call HTTP directly.

Assume these shell variables are held by the product backend or local operator:

```sh
HELPAFFE_URL=https://support.example.test
HELPAFFE_PRODUCT_KEY=hfp_replace-with-a-server-side-secret
EXTERNAL_USER_ID=stable-customer-7
```

Create a ticket with a client-generated request id. Reuse that id when retrying
the same request after a connection failure.

```sh
curl "$HELPAFFE_URL/api/product/tickets" \
  -X POST \
  -H "Authorization: Bearer $HELPAFFE_PRODUCT_KEY" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 01J-example-create' \
  -d "{\"external_user_id\":\"$EXTERNAL_USER_ID\",\"name\":\"Ada User\",\"email\":\"ada@example.test\",\"subject\":\"Settings are blank\",\"message\":\"I cannot open settings.\",\"context\":{\"product_version\":\"2.4.1\",\"page\":\"/settings\",\"browser\":\"Firefox\"}}"
```

The optional `context` is an immutable JSON object of at most 16 KiB. Do not
put passwords, session tokens, product API keys, or other credentials in it.

List and read tickets by the stable external user id. The key supplies the
project, so no project id is accepted.

```sh
curl --get "$HELPAFFE_URL/api/product/tickets" \
  -H "Authorization: Bearer $HELPAFFE_PRODUCT_KEY" \
  --data-urlencode "external_user_id=$EXTERNAL_USER_ID"

curl --get "$HELPAFFE_URL/api/product/tickets/HLP-0123456789ABCDEF0123" \
  -H "Authorization: Bearer $HELPAFFE_PRODUCT_KEY" \
  --data-urlencode "external_user_id=$EXTERNAL_USER_ID"
```

A single-ticket response carries both `summary.version` and an `ETag` such as
`"3"`. Send that version plus another stable request id with a customer reply:

```sh
curl "$HELPAFFE_URL/api/product/tickets/HLP-0123456789ABCDEF0123/replies" \
  -X POST \
  -H "Authorization: Bearer $HELPAFFE_PRODUCT_KEY" \
  -H 'Content-Type: application/json' \
  -H 'If-Match: "3"' \
  -H 'Idempotency-Key: 01J-example-reply' \
  -d "{\"external_user_id\":\"$EXTERNAL_USER_ID\",\"message\":\"The issue still happens.\"}"
```

If another actor changed the ticket, helpaffe returns `412 stale` with
`current_version`; read again before retrying. API failures use
`application/problem+json`. Product responses contain only customer messages
and public support replies—never internal notes, support instructions,
assignees, or administration.
