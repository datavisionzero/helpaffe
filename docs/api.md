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
| `POST /api/product/tickets` | Create an Open ticket for an identified end user, with an optional JSON context snapshot |
| `GET /api/product/tickets?external_user_id=...` | List only that end user's tickets in the key's project |
| `GET /api/product/tickets/{number}?external_user_id=...` | Read that end user's ticket and public conversation |
| `POST /api/product/tickets/{number}/replies` | Add a customer message to that end user's ticket |

Administrator and administrator-agent notification configuration is defined in
the backoffice OpenAPI contract and described in
[`email.md`](email.md). Support users and their agents cannot read SMTP or
template configuration, even for projects they can support.

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
| `GET /api/backoffice/assignees` | List active support users eligible in the visible project scope, optionally for one project |
| `GET /api/backoffice/tickets` | Filter visible tickets by status, priority, project, assignee, `mine`, or PostgreSQL full-text search with a bound cursor |
| `GET /api/backoffice/tickets/{number}` | Read the requester, technical creation context, complete conversation, delivery history, actor attribution, and project support instructions together |
| `GET /api/backoffice/tickets/{number}/requester-tickets` | List other tickets for the same stable requester ID within that ticket's project |
| `POST /api/backoffice/tickets/next` | Atomically assign and start the urgent-first, longest-waiting eligible Open ticket |
| `GET /api/backoffice/tickets/wait` | Wait up to 60 seconds for eligible open work or a customer reply after an opaque cursor, without acquiring it |
| `PATCH /api/backoffice/tickets/{number}` | Change status, priority, or eligible human assignee |
| `PUT /api/backoffice/tickets/{number}/snooze` | Set a future snooze instant or clear it with `null` |
| `POST /api/backoffice/tickets/{number}/development-references` | Append a typed Planaffe, GitHub, or GitLab HTTPS task reference |
| `DELETE /api/backoffice/tickets/{number}/development-references/{referenceId}` | Remove one task reference |
| `POST /api/backoffice/tickets/{number}/replies` | Atomically add a public reply and its resulting status |
| `POST /api/backoffice/tickets/{number}/notes` | Add an internal support note |
| `POST /api/backoffice/tickets/{number}/notifications/{notificationId}/retry` | Retry one failed email delivery without changing the ticket version or conversation |
| `GET /api/backoffice/projects/{projectId}/support-instructions` | Read project Markdown instructions within current project scope |
| `PUT /api/backoffice/projects/{projectId}/support-instructions` | Replace instructions as an administrator or administrator agent |
| `GET /api/backoffice/projects/{projectId}/solutions` | List or full-text search internal solution articles in one visible project |
| `POST /api/backoffice/projects/{projectId}/solutions` | Create a project-local Markdown solution article |
| `GET /api/backoffice/projects/{projectId}/solutions/{key}` | Read one solution article by its stable key |
| `PUT /api/backoffice/projects/{projectId}/solutions/{key}` | Replace an article's title and Markdown with version checking |
| `DELETE /api/backoffice/projects/{projectId}/solutions/{key}` | Delete an obsolete article with version checking |

Ticket resources outside the caller's current human-and-agent project
intersection return `not-found`. An assignee must be active and currently able
to access the ticket's project. The assignee lookup applies the same project
visibility and eligibility rules without exposing account administration.
`mine=true` means tickets assigned to the human user, including work performed
for that user by any of their named agents.

The `search` parameter uses PostgreSQL web-style full-text parsing over ticket
number, subject, requester name and email, and conversation text. It composes
with project and status filters; GIN expression indexes cover ticket metadata
and conversation bodies. Requester history derives both the project and stable
external requester ID from a currently visible ticket, excludes that source
ticket, and never joins the same external ID across projects.

`next` considers only Open tickets that are unassigned or already assigned to
the responsible human. It orders Urgent before Normal, then by `waiting_since`;
row locking with skip-locked selection prevents parallel agents from acquiring
the same ticket. Acquisition changes the ticket to In Progress and never
expires automatically.

An active `snoozed_until` leaves the ticket status unchanged but excludes the
ticket from the normal list, full-text search, and `next` acquisition. Direct
reads and requester history still expose it. The ticket becomes eligible again
as soon as the instant passes, without a background job. A new customer reply
clears an active snooze immediately. Setting and clearing the value is recorded
as an internal system event.

Development references are ordered by insertion and contain only a fixed type,
an HTTPS URL, and a short display label. They are support-only ticket context:
helpaffe neither contacts nor synchronizes the referenced system. Adding and
removing a reference creates an internal system event, and duplicate URLs on a
ticket are rejected.

Solution articles are internal, project-scoped Markdown documents addressed by
an immutable lowercase key. Support users and their agents can create, read,
search, update, and delete them within their effective project scope. Lists use
most-recently-updated ordering and actor-, project-, and search-bound cursors;
search uses PostgreSQL web-style full-text matching over title and Markdown.
Reads return the positive article version and matching ETag. Updates and deletes
require that version in `If-Match`, so a concurrent edit is rejected as stale.

`wait` returns immediately when an eligible Open ticket exists. Otherwise it
waits for a customer reply newer than its actor- and project-bound cursor. A
request without a cursor starts watching from the request instant, so old
customer activity is not replayed. Each response carries the next cursor. A
timeout is a successful empty result (`work: null`, `timed_out: true`), while
request cancellation ends the server wait. In-process change notifications wake
all waiters; a one-second fallback scan covers snooze expiry and access changes
without aggressive client polling. Waiting never assigns or changes a ticket.

Ticket reads return `ETag: "N"` and the same positive `version` in the body.
Ticket mutations require that value in `If-Match`; omission returns
`version-required`, while a concurrent change returns `stale` with
`current_version`. Replies, notes, and next-ticket acquisition also require an
`Idempotency-Key`; snooze and development-reference changes require it as well.
Repeating the same request for 24 hours returns its original
body and ETag without adding another conversation entry; reuse for a different
request returns `idempotency-mismatch`.

## Product ticket operations

The checked-in [`product.openapi.json`](api/product.openapi.json) is the
server-to-server contract for product integrations. Every call uses the project
from the `hfp_` product key; a request cannot provide or override a project id.
The product backend supplies its authenticated user's stable
`external_user_id`. Ticket lists, reads, and replies match both that id and the
key's project, returning `not-found` rather than revealing a ticket across a
boundary.

Creation records the supplied name and email as ticket requester details. The
identity remains `(project, external_user_id)`, so later tickets with a changed
email still belong to the same requester history. The optional `context` must
be a JSON object whose UTF-8 representation is at most 16 KiB. It is stored as
an immutable creation snapshot and returned in support ticket context; it is
not a set of mutable custom fields.

Product reads expose only customer messages and public support replies. They do
not expose internal notes, system events, support instructions, assignees, or
administration. Product ticket creation and customer replies require an
`Idempotency-Key`; replies additionally require the latest ticket ETag in
`If-Match`. A customer reply reopens Waiting for Customer or Resolved tickets,
while an In Progress or already Open ticket retains its status.
