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

## Full Compose stack

Create the local environment file, replace its example password, and start the
application and database:

```sh
cp deploy/.env.example deploy/.env
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up --build -d
curl --fail http://localhost:5066/health/ready
```

PostgreSQL data lives in the named `helpaffe-db` volume. An ordinary
`docker compose ... down` and subsequent `up -d` reuses it. `down --volumes`
deletes the database and is reserved for intentionally starting over.

The stack contains the helpaffe application and PostgreSQL only. The MVP has no
object store, upload service, or language service.

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
