# Development and Operations

## Toolchains

Install the .NET SDK selected by `global.json`, Node.js 24, Go selected by
`src/cli/go.mod`, and Docker. Restore and verify all three codebases with:

```sh
dotnet tool restore
dotnet restore Helpaffe.slnx
dotnet build Helpaffe.slnx
dotnet test tests/Helpaffe.UnitTests
dotnet test tests/Helpaffe.IntegrationTests

cd src/web
npm ci
npm run typecheck
npm test
npm run build

cd ../cli
go vet ./...
go test ./...
go build ./cmd/helpaffe
```

The integration tests use Testcontainers and require a running Docker engine.

## Local development

Start PostgreSQL, then run the API and web development server in separate
terminals:

```sh
docker compose -f deploy/docker-compose.dev.yml up -d
dotnet run --project src/Helpaffe.Api --urls http://localhost:5066

cd src/web
npm run dev
```

The API applies forward-only EF Core migrations before it accepts requests.
`http://localhost:5066/health/live` reports process liveness;
`http://localhost:5066/health/ready` succeeds only after PostgreSQL is reachable.

Add a migration after changing the persistence model with:

```sh
dotnet ef migrations add Name \
  --project src/Helpaffe.Infrastructure \
  --startup-project src/Helpaffe.Infrastructure \
  --output-dir Persistence/Migrations
```

## Published Compose stack

For a self-hosted installation using the released multi-architecture image,
follow [`install.md`](install.md). Its `deploy/compose.yaml` downloads the
application from GHCR and works without a source checkout.

## Full source-built Compose stack

For development from this checkout, create the local environment file, fill in
all four required values, and build the application and database locally:

```sh
cp deploy/.env.example deploy/.env
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up --build -d
curl --fail http://localhost:5066/health/ready
```

PostgreSQL data lives in the named `helpaffe-db` volume and attachment bytes in
the named `helpaffe-attachments` volume. Attachment metadata remains in
PostgreSQL. An ordinary `docker compose ... down` and subsequent `up -d` reuses
both volumes. `down --volumes`
deletes the database and is reserved for intentionally starting over.

The stack contains the helpaffe application and PostgreSQL only. File bytes are
written by the application to `Attachments:RootPath`; the production Compose
file mounts that path from the attachment volume. There is no separate object
store, upload service, or language service.

## Application logs

The API writes JSON logs to the console, including notification worker errors.
Read them with `docker compose logs helpaffe` for the published stack, or with
`docker compose --env-file deploy/.env -f deploy/docker-compose.yml logs helpaffe`
for a source build. To also deliver entries to a logaffe installation, set both
`HELPAFFE_LOGAFFE_URL` and `HELPAFFE_LOGAFFE_TOKEN` in the deployment `.env` and
restart the application.
The URL is the absolute HTTP or HTTPS address of the installation; the client
appends the ingest path. The token is a secret and selects the logaffe project.
Keep it in the deployment secret store, never in source control or application
logs.

Both Compose variants pass these values to `Observability:Logaffe:Url` and
`Observability:Logaffe:IngestToken`. If both are empty, only console logging is
active. If one is missing or the URL is invalid, startup fails with a
configuration error. Logaffe delivery uses a bounded in-memory queue; console
logs remain available if delivery fails.

## SMTP secret encryption

Set `HELPAFFE_SECRETS_ENCRYPTION_KEY` to one Base64-encoded 32-byte value before
an administrator stores SMTP credentials. Generate it once, for example with
`openssl rand -base64 32`, place it in the deployment's secret store, and keep
it stable for the lifetime of the database. The application uses it for
authenticated encryption of project SMTP passwords; API, Web, and CLI reads
return only `password_configured` and never return plaintext or ciphertext.

Losing or changing this key makes existing SMTP passwords unreadable. In that
case restore the original key or replace each project's SMTP password after
configuring a new key. Do not commit the key to this repository or expose it to
product backends and browsers.

Ticket email is dispatched from a durable PostgreSQL outbox by a worker in the
application process. New work is attempted immediately; transient SMTP errors
are retried after 1, 5, and 30 minutes and then remain visible as `failed` until
support retries them from the ticket. SMTP downtime does not roll back ticket
writes. Restarting the application resumes pending jobs automatically. Monitor
failed delivery state through ticket detail; failure text is intentionally
sanitized and never contains SMTP credentials.

## Initial administrator and access management

On the first application start against an empty database, helpaffe creates one
administrator from `HELPAFFE_BOOTSTRAP_NAME`, `HELPAFFE_BOOTSTRAP_EMAIL`, and
`HELPAFFE_BOOTSTRAP_PASSWORD`. Use a unique password of at least 12 characters.
The bootstrap values are ignored as soon as any user exists, so changing the
environment later does not reset an account or create another administrator.
They may be removed from the runtime environment after the first successful
sign-in.

Open `http://localhost:5066`, sign in, and use the **People** section to create
users, choose the fixed Administrator or Support role, deactivate accounts, and
assign projects to support users. Administrators can see every project. Support
users see only projects explicitly assigned to them. Account deactivation and
project-access removal take effect on the next request; deactivation also ends
all of that user's browser sessions.

At least one active administrator must remain. The application rejects attempts
to deactivate or demote the last active administrator. Passwords and session
tokens are stored only as hashes and must never be placed in logs or committed
environment files.

## Agent and product credentials

Every automation must have its own named agent credential. A support user can
create and revoke credentials for their own account in **Agent credentials**;
an administrator can choose any user as the owner. Select either all projects
the owner may access or an explicit subset. The all-projects option follows
future grants automatically. Both modes are always intersected with the
owner's current access.

Administrators create project-bound integration keys in **Product API keys**.
Keep at least two keys active during a rotation, update the product backend,
verify it uses the new key, and then revoke the old key. Product keys work only
under `/api/product`; agent tokens work only under `/api/backoffice`.

Agent tokens and product API keys are shown exactly once after creation. Copy
them directly into the intended secret store. helpaffe persists only a SHA-256
hash and a short display prefix, so a lost value cannot be recovered and must
be replaced. Never put a token in source control, screenshots, command history,
URLs, or application logs. Revocation and owner deactivation take effect on the
next request.

## Persistence smoke test

The repeatable verification for an empty database and a restart is:

```sh
docker compose --env-file deploy/.env -f deploy/docker-compose.yml down --volumes
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up --build -d
curl --fail http://localhost:5066/health/ready
docker compose --env-file deploy/.env -f deploy/docker-compose.yml restart
curl --retry 20 --retry-delay 1 --retry-connrefused --fail \
  http://localhost:5066/health/ready
```

The integration test `Empty_database_is_migrated_and_readiness_is_healthy`
covers the same empty-database migration path in CI.

## Database backup and restore

Back up PostgreSQL and the attachment storage together so metadata never points
at a different generation of file content. Keep the matching
`HELPAFFE_SECRETS_ENCRYPTION_KEY` in the deployment secret store: a database
dump without that key cannot decrypt stored SMTP passwords. Create and inspect
a custom-format dump with:

```sh
docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
  exec -T db pg_dump -U helpaffe -d helpaffe \
  --format=custom --no-owner --no-privileges > helpaffe.dump

docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
  exec -T db pg_restore --list < helpaffe.dump
```

Copy dumps to access-controlled, encrypted storage and test restores on a
separate environment. A restore replaces the current database and therefore
requires an announced maintenance window. After confirming the target and dump:

```sh
docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
  stop helpaffe

docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
  exec -T db dropdb -U helpaffe --force helpaffe
docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
  exec -T db createdb -U helpaffe helpaffe
docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
  exec -T db pg_restore -U helpaffe -d helpaffe \
  --exit-on-error --no-owner --no-privileges < helpaffe.dump

docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
  up -d helpaffe
curl --retry 20 --retry-delay 1 --retry-connrefused --fail \
  http://localhost:5066/health/ready
```

Restore with an application version at least as new as the version that wrote
the dump. Startup applies any later forward migrations. Verify sign-in, one
ticket read, and one configured SMTP test after restore before ending the
maintenance window.

## Running a named agent

Build or install the CLI on the agent host, create a distinct named credential
owned by the responsible support user, and inject it from the host's secret
store. Never pass the token as a command-line flag or bake it into an image.

```sh
export HELPAFFE_URL=https://support.example.test
export HELPAFFE_TOKEN=hfa_replace-from-secret-store
helpaffe status
helpaffe me
helpaffe project list
helpaffe ticket next --project PROJECT_UUID
```

Use `ticket get` before every mutation and pass its current version through
`--version`. Supply long replies and notes through `--message-file` or
`--note-file`; use `-` only when the harness intentionally provides stdin.
Treat exit 6 as a stale-version signal: read again, reconsider the action, and
never overwrite the concurrent change automatically. Revoke a credential when
the automation is retired or suspected to be exposed. See
[`cli.md`](cli.md) for commands and stable exit codes, and
[`acceptance.md`](acceptance.md) for the complete two-product walkthrough.
