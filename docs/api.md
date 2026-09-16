# HTTP Contract Conventions

helpaffe exposes two deliberately separate HTTP surfaces from one application.
They call the same application use cases where behavior overlaps, but they have
different credentials, response shapes, and visibility rules.

## Route families and contracts

| Prefix | Caller | Purpose | Contract |
| --- | --- | --- | --- |
| `/api/backoffice` | browser session, human CLI token, or named agent token | Support work and permitted configuration | `docs/api/backoffice.openapi.json` |
| `/api/product` | a project's product API key | End-user ticket integration for that project | `docs/api/product.openapi.json` |
| `/health/live` | unauthenticated | Process liveness | not a product contract |
| `/health/ready` | unauthenticated | Database and migration readiness | not a product contract |

The route group is an authorization boundary. Product credentials never
authenticate on backoffice routes. Human and agent credentials never
authenticate on product routes. The product contract contains no internal note,
support instruction, user administration, agent administration, or cross-project
operation. Browser and CLI clients use the same backoffice operations and rules.

The API has no version segment. The server reports its release version in a
`Helpaffe-Version` response header and at `/api/backoffice/version`. Database
migrations only move forward. A future incompatible contract requires a new
decision rather than a dormant `/v1` prefix.

## Actors and authorization

The fixed human roles are `administrator` and `support`. An administrator sees
all projects. A support user sees assigned projects. A named agent acts for one
human user and receives the intersection of the user's current project access
and either all allowed projects or the agent's configured subset. User
deactivation, role changes, project removal, and agent revocation take effect on
the next request.

History records both the responsible user and, when present, the acting agent.
A product request records the product credential and end-user reference, but a
product credential is never treated as a support actor or role.

Human-only administration is exposed only to a browser session: user and access
management, all credential creation and revocation, and permanent deletion.
Authorization is enforced in application use cases as well as at the route
group so another adapter cannot bypass it.

## Shapes and names

JSON uses UTF-8 and `snake_case`. Timestamps are UTC RFC 3339 strings with a
`Z` suffix. Identifiers are opaque strings. Closed request objects reject an
unknown member. List responses contain summary shapes; a single-resource read
contains the complete shape allowed on that surface.

An end user is addressed only by `(project_id, external_user_id)`. Product
requests take the external user id from the authenticated product backend and
never accept a project id that can override the product key's project.

## Problems

Every HTTP error uses `application/problem+json` in the RFC 9457 shape:

```json
{
  "type": "/problems/stale",
  "title": "The ticket changed",
  "status": 412,
  "detail": "Read the current ticket and retry with its version.",
  "instance": "/api/backoffice/tickets/HLP-42",
  "current_version": 8
}
```

`type` is a stable relative URI; its final segment is the machine-readable code.
`title` and `detail` are English text for a person. Extension members carry the
data needed to recover. The initial status conventions are:

| Status | Code | Meaning |
| --- | --- | --- |
| 400 | `validation`, `unknown-field`, `cursor-invalid` | The request is malformed or violates a field limit |
| 401 | `unauthenticated` | The credential is absent, unknown, expired, or revoked |
| 403 | `forbidden`, `csrf` | The authenticated actor cannot perform the action |
| 404 | `not-found` | The resource does not exist or is outside the caller's visible scope |
| 409 | `idempotency-mismatch` | A request key was reused for another request |
| 412 | `stale` | `If-Match` does not match the current ticket version |
| 422 | `transition` | The requested domain transition is not allowed |
| 428 | `version-required` | A ticket mutation omitted `If-Match` |
| 429 | `throttled` | A rate limit was reached; `Retry-After` is present |
| 500 | `internal` | An unexpected server failure, with no sensitive detail |

Resources outside a caller's project scope use `not-found` so the contract does
not disclose their existence. A known resource on which the caller lacks a
permitted action uses `forbidden`.

## Pagination

Potentially growing collections use opaque cursor pagination:

```json
{
  "items": [],
  "next_cursor": null
}
```

`limit` defaults to 50 and is capped at 100. `cursor` is an opaque value emitted
by the server. It is bound to the route, caller scope, filters, and ordering; a
cursor reused with different inputs is `cursor-invalid`. Each endpoint declares
one stable default order and appends the immutable id as its final tie-breaker.
Small administrative collections may explicitly state that they are unpaginated.

## Ticket versions

Every ticket has a positive, monotonically increasing integer `version`. A
single-ticket response includes `version` and an `ETag` containing its quoted
decimal form, for example `ETag: "8"`. Every mutation of a ticket or its
conversation requires `If-Match` with the version last read. A missing header is
`version-required`; a mismatch is `stale` and reports `current_version`.

A successful mutation increments the version once for the whole transaction,
including status change, assignment, message, history, and queued notification.
The complete updated ticket is returned with its new `ETag`. This convention is
identical on product and backoffice routes and prevents a late customer reply or
support action from being silently overwritten.

## Idempotency

Every authenticated write accepts `Idempotency-Key`, an opaque client-generated
string of at most 200 characters. It is required for message creation, atomic
next-ticket acquisition, and notification retry. Official clients send it on
all writes.

The server stores the original status, selected response headers, and body for
24 hours under the authenticated credential and key. Repeating the same method,
normalized path, query, and body returns that response. Reusing the key for a
different request is `idempotency-mismatch`. Product keys, users, and named
agents have separate key spaces. Idempotency and the `If-Match` check execute in
the same transaction as the domain act.

## Authentication transports

The browser uses a secure, HTTP-only, SameSite cookie and a CSRF header on
writes. A human CLI obtains a revocable user token through device login and
stores it in the operating system keychain. Named agents receive their own
revocable bearer token through their harness. Product backends receive
project-bound bearer keys. Only hashes and non-secret prefixes are stored.

Bearer credentials are refused over plain HTTP outside loopback by official
clients. Authentication logs contain credential kind, prefix, and actor id, but
never a secret, session cookie, password, SMTP password, or full authorization
header.

## Initial access operations

The first backoffice slice uses these access operations. Browser writes after
sign-in require `X-Helpaffe-CSRF: 1`; bearer-authenticated agent writes do not.
User, access, agent-credential, and product-key management is human-only even
when an agent belongs to an administrator.

| Method and path | Purpose |
| --- | --- |
| `POST /api/backoffice/session` | Sign in with email and password and create the HTTP-only session cookie |
| `DELETE /api/backoffice/session` | Revoke the current session and clear its cookie |
| `GET /api/backoffice/me` | Read the signed-in user |
| `GET /api/backoffice/projects` | List every project for an administrator or assigned projects for support |
| `POST /api/backoffice/projects` | Create a project as an administrator |
| `PATCH /api/backoffice/projects/{id}` | Change a visible project's key or name as an administrator or administrator agent |
| `GET /api/backoffice/users` | List users and their project assignments as an administrator |
| `POST /api/backoffice/users` | Create an administrator or support user |
| `PATCH /api/backoffice/users/{id}` | Change role or activation state |
| `PUT /api/backoffice/users/{userId}/projects/{projectId}` | Grant project access |
| `DELETE /api/backoffice/users/{userId}/projects/{projectId}` | Revoke project access |
| `GET /api/backoffice/agents` | List the signed-in human's agents, or all agents for an administrator |
| `POST /api/backoffice/agents` | Create a named agent credential and return its token once |
| `DELETE /api/backoffice/agents/{id}` | Revoke an owned agent, or any agent as an administrator |
| `GET /api/backoffice/product-keys` | List product-key metadata as an administrator |
| `POST /api/backoffice/projects/{projectId}/product-keys` | Create a project-bound product key and return it once |
| `DELETE /api/backoffice/product-keys/{id}` | Revoke one product key without affecting the project's other keys |
| `GET /api/product/project` | Read the project bound to the presented product API key |

User creation requires a password of at least 12 characters. The final active
administrator cannot be deactivated or changed to support. Deactivating a user
revokes all browser sessions immediately.

Named agent tokens begin with `hfa_` and authenticate only on the backoffice
route family. Product API keys begin with `hfp_` and authenticate only on the
product route family. The complete value is present only in a successful create
response; subsequent reads expose its non-secret prefix. Multiple product keys
may remain active for one project so callers can rotate them without downtime.

An agent configured for all allowed projects evaluates its owner's current role
and project access on every request, so later grants are included automatically.
A selected-project agent receives the intersection of its stored selection and
the owner's current access. Revocation, user deactivation, role changes, and
project-access removal therefore take effect on the next request.

## Ticket support operations

The checked-in [`backoffice.openapi.json`](api/backoffice.openapi.json) defines
the support contract shared by the web application and CLI. Its first ticket
operations are:

| Method and path | Purpose |
| --- | --- |
| `GET /api/backoffice/tickets` | Filter visible tickets by status, priority, project, assignee, `mine`, or text search with a bound cursor |
| `GET /api/backoffice/tickets/{number}` | Read the requester, complete conversation, actor attribution, and project support instructions together |
| `POST /api/backoffice/tickets/next` | Atomically assign and start the urgent-first, longest-waiting eligible Open ticket |
| `PATCH /api/backoffice/tickets/{number}` | Change status, priority, or eligible human assignee |
| `POST /api/backoffice/tickets/{number}/replies` | Atomically add a public reply and its resulting status |
| `POST /api/backoffice/tickets/{number}/notes` | Add an internal support note |
| `GET /api/backoffice/projects/{projectId}/support-instructions` | Read project Markdown instructions within current project scope |
| `PUT /api/backoffice/projects/{projectId}/support-instructions` | Replace instructions as an administrator or administrator agent |

Ticket resources outside the caller's current human-and-agent project
intersection return `not-found`. An assignee must be active and currently able
to access the ticket's project. `mine=true` means tickets assigned to the human
user, including work performed for that user by any of their named agents.

`next` considers only Open tickets that are unassigned or already assigned to
the responsible human. It orders Urgent before Normal, then by `waiting_since`;
row locking with skip-locked selection prevents parallel agents from acquiring
the same ticket. Acquisition changes the ticket to In Progress and never
expires automatically.

Ticket reads return `ETag: "N"` and the same positive `version` in the body.
Ticket mutations require that value in `If-Match`; omission returns
`version-required`, while a concurrent change returns `stale` with
`current_version`. Replies, notes, and next-ticket acquisition also require an
`Idempotency-Key`. Repeating the same request for 24 hours returns its original
body and ETag without adding another conversation entry; reuse for a different
request returns `idempotency-mismatch`.
