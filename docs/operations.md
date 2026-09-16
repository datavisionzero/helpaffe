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
