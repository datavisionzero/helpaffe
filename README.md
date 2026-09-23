# helpaffe

Helpdesk made easy and agentic for self-hosters.

## Current release

Version `0.1.2` is the current self-hosted release. The application image is
published for `linux/amd64` and `linux/arm64` as
`ghcr.io/datavisionzero/helpaffe:0.1.2`; matching `helpaffe` CLI archives are
attached to the [GitHub release](https://github.com/datavisionzero/helpaffe/releases).
This is a `0.x` release: check the notes and back up data before upgrading.
This release introduces a responsive application shell, light and dark themes,
and more compact ticket, solution, administration, and email settings views.

An installation needs only the published Compose file and an environment file,
not this source checkout:

```sh
mkdir helpaffe && cd helpaffe
base=https://raw.githubusercontent.com/datavisionzero/helpaffe/v0.1.2/deploy
curl -fsSLo compose.yaml "$base/compose.yaml"
curl -fsSLo .env "$base/.env.example"
chmod 600 .env
# Fill in the four required values in .env, then:
docker compose up -d --wait
```

The application listens on `127.0.0.1:5066` by default. Use a TLS reverse
proxy for remote access. The [installation guide](docs/install.md) covers
configuration, first login, the CLI, backups, and upgrades.

The later-roadmap capabilities for grouping tickets about one problem and
accepting customer replies by email are not part of this release. Products
integrate from their own authenticated backends; the included product example
uses placeholder authentication and is not a production login system.

## Development

The product direction is described in [`VISION.md`](VISION.md). The initial
technical foundation is documented in [`docs/codebase.md`](docs/codebase.md),
with the [domain language](CONTEXT.md), [HTTP](docs/api.md), and
[CLI](docs/cli.md) conventions beside it.

Product backends can integrate through the server-side
[`Helpaffe.Sdk`](src/Helpaffe.Sdk/README.md) (available as a
[NuGet package](https://www.nuget.org/packages/Helpaffe.Sdk)) or the documented
[Product API HTTP calls](docs/product-integration.md). A runnable
[ASP.NET Core example](examples/Helpaffe.ProductExample/README.md) keeps the
product key behind its own authenticated backend.

Build, test, and local Compose instructions are in
[`docs/operations.md`](docs/operations.md). The reproducible first complete
product-to-support acceptance is in
[`docs/acceptance.md`](docs/acceptance.md).
