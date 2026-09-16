# The Codebase

helpaffe follows the layout already exercised by planaffe and hostingaffe. The
three clients share HTTP contracts and domain behavior, not source-level domain
types.

## Toolchain baseline

The initial baseline is the version set used by both local sibling checkouts on
2026-09-16:

| Area | Baseline |
| --- | --- |
| Backend | .NET SDK 10.0.100 with `latestFeature` roll-forward; `net10.0`; EF Core 10.0.11; Npgsql provider 10.0.3 |
| Database | PostgreSQL 18 with data checksums |
| Web | Node.js 24; React 19.2.6; TypeScript 5.9.3; Vite 8; Tailwind CSS 4; Base UI 1.7 |
| CLI | Go 1.27.1; Cobra 1.10.2; oapi-codegen 2.8.0 |
| Tests | xUnit v3, ASP.NET Core test host and Testcontainers PostgreSQL; Vitest and Testing Library; the Go standard test tool |

Exact dependency versions are centrally pinned and committed. A version change
is reviewed as a dependency change and updates this table when it changes a
toolchain baseline.

## Repository shape

```text
src/Helpaffe.Domain             domain rules, no package references
src/Helpaffe.Application        use cases and ports
src/Helpaffe.Infrastructure     PostgreSQL, identity, email, and other adapters
src/Helpaffe.Api                HTTP adapters and composition root
src/Helpaffe.Sdk                server-side .NET product-integration SDK
src/cli                         the `helpaffe` Go CLI
src/web                         the React application
tests/Helpaffe.UnitTests        Domain and Application without infrastructure
tests/Helpaffe.IntegrationTests API, persistence, migrations, and concurrency
tests/Helpaffe.Sdk.Tests        SDK request and contract behavior
deploy                          Dockerfile, Compose, and environment example
docs/api                        checked-in backoffice and product OpenAPI documents
```

Dependencies point inward:

```text
Api ─────────► Application ─────────► Domain
 └───────────► Infrastructure ──────►

Sdk ─────────► HTTP product contract only
CLI ─────────► HTTP backoffice contract only
Web ─────────► HTTP backoffice contract only
```

`Helpaffe.Domain` contains rules that are true regardless of the caller: ticket
status transitions, project membership, actor attribution, ticket versioning,
and credential scope. `Helpaffe.Application` contains one use case per act and
coarse ports for operations that must be atomic. `Helpaffe.Infrastructure`
implements those ports with EF Core and PostgreSQL. `Helpaffe.Api` authenticates
the caller, applies the route-family boundary, maps problem documents, and wires
the application together.

The web application and CLI are clients of the backoffice contract. The .NET
SDK is a client of the product contract and must have no backoffice operations
in its generated or public surface. Generated client code is build output and
is not committed; the OpenAPI documents are committed and contract-tested
against a running API.

## Test boundary

Unit tests cover domain rules and application use cases through substituted
ports. Integration tests use a real PostgreSQL instance through Testcontainers
for migrations, constraints, credential boundaries, idempotency, pagination,
and concurrent ticket writes. The browser tests cover component behavior and
the generated backoffice client. CLI tests cover argument validation, rendering,
problem-to-exit-code mapping, and HTTP behavior. SDK tests prove that the product
client cannot address backoffice routes and preserves idempotency and ticket
versions.

Contract tests capture both OpenAPI documents from a running instance and fail
when either differs structurally from its checked-in file. Architecture tests
read the project references and fail on an outward dependency or a package
reference from Domain.

## Build and implementation sequence

The first implementation slices are deliberately executable and keep the trunk
buildable after each slice:

1. Copy the hostingaffe foundation, rename it mechanically, remove its product
   domain, and retain only the four-layer host, React shell, Go command root,
   tests, CI, Dockerfile, and Compose shape. Pin the baseline above and expose
   liveness and database-readiness checks.
2. Establish the two empty OpenAPI route groups and contract-capture tests. Add
   browser sessions, bootstrap of the first administrator, fixed human roles,
   project access, and centralized authorization to the backoffice group.
3. Add named agent credentials and project scopes, then product credentials in
   a separate persistence model and authentication handler. Prove that each
   credential kind is rejected by the other route group.
4. Introduce the project and ticket aggregates, integer ticket versions,
   idempotency storage, cursor queries, atomic next-ticket acquisition, and
   message history before adding UI screens or CLI verbs.
5. Add web, CLI, and .NET SDK features only through the checked-in contracts.
   Each feature lands with the domain rule, HTTP operation, relevant client
   surface, and focused unit or integration coverage.

No object storage, upload endpoint, language catalogue, permission-matrix
framework, or post-MVP workflow is part of these slices.
