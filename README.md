# helpaffe

Helpdesk made easy and agentic for self-hosters.

The product direction is described in [`VISION.md`](VISION.md). The initial
technical foundation is documented in [`docs/codebase.md`](docs/codebase.md),
with the [domain language](CONTEXT.md), [HTTP](docs/api.md), and
[CLI](docs/cli.md) conventions beside it.

Product backends can integrate through the server-side
[`Helpaffe.Sdk`](src/Helpaffe.Sdk/README.md) or the documented
[Product API HTTP calls](docs/product-integration.md). A runnable
[ASP.NET Core example](examples/Helpaffe.ProductExample/README.md) keeps the
product key behind its own authenticated backend.

Build, test, and local Compose instructions are in
[`docs/operations.md`](docs/operations.md).
