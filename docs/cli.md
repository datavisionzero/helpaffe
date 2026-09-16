# The CLI

`helpaffe` is the single console client for permitted support and administrative
work. It is a standalone Go binary and a client of the backoffice HTTP contract;
it never connects to PostgreSQL and never accepts a product API key.

## Name and command shape

The executable uses the full name `helpaffe`. The natural short name `hf` is
already the [official Hugging Face CLI](https://huggingface.co/docs/huggingface_hub/en/guides/cli),
while `ha` and `pa` are the sibling hostingaffe and planaffe tools. Searches of
the Homebrew formula index, Debian package index, and general web results on
2026-09-16 found no established `helpaffe` command. The full name is therefore
the least surprising and avoids an alias that another ecosystem already owns.

Commands follow `helpaffe <object> <verb>`, with singular domain names. The
first release is intentionally agent-only: its global commands are `status`,
`me`, and `version`. Human login and human-administration commands are not part
of the agent executable surface.

## Authentication and configuration

An agent harness supplies a named agent credential. Credential and instance
resolution are deterministic:

1. Instance: `--url`, then `HELPAFFE_URL`.
2. Token: `HELPAFFE_TOKEN`, then `--token-file PATH`.

Token files must have mode `0600` on Unix-like systems. `helpaffe status` prints
only the selected source names and never prints a credential. Product keys
(`hfp_…`) are rejected locally; named agent tokens use the `hfa_…` prefix.

## Commands

| Command | Purpose |
| --- | --- |
| `project list` | List projects in the agent's scope |
| `project create`, `project update` | Manage projects as an administrator agent |
| `project instructions get`, `project instructions set` | Read or replace support instructions |
| `project email settings get`, `project email settings set` | Read or replace project SMTP, sender, recipient, branding, and link settings |
| `project email template list`, `get`, `set`, `preview` | Manage and render the five fixed English notification templates |
| `project email test` | Submit one rendered test message through project SMTP |
| `ticket list` | Filter and search visible tickets |
| `ticket get` / `ticket context` | Read the full ticket conversation, technical creation context, and instructions |
| `ticket requester-tickets` / `ticket related` | List other tickets for the same requester within the source ticket's project |
| `ticket next` | Atomically acquire the next eligible ticket |
| `ticket reply`, `ticket note` | Add a public reply or internal note |
| `ticket update` | Change status, priority, or assignee |
| `ticket snooze NUMBER --until DATE_TIME`, `ticket unsnooze NUMBER` | Set or clear the UTC instant until which a ticket stays out of normal work queues |
| `ticket reference add`, `ticket reference remove` | Attach or remove typed HTTPS development-task references; `ticket get` displays them |
| `ticket resolve`, `ticket reopen` | Resolve or reopen a ticket |
| `ticket notification retry NUMBER NOTIFICATION_ID` | Queue a failed email delivery for an immediate retry |

Human-only administration of users, credentials, and project grants remains in
the browser interface and is deliberately absent from the CLI.

## Agent-facing behavior

- Commands never prompt, open an editor, or start a pager. Device login waits
  after printing its code but does not read stdin.
- Domain data goes to stdout. Diagnostics and errors go to stderr.
- `--json` writes exactly one JSON value on stdout on success. On failure it
  writes the problem document as one JSON value on stderr and leaves stdout
  empty.
- Human-readable output is stable enough to read, but scripts depend only on
  `--json` and exit codes.
- Acquisition, public replies, internal notes, snooze and development-reference changes, and notification retries get a
  fresh UUID idempotency key per invocation.
- Ticket writes require `--version N`; the client sends `If-Match: "N"` and
  reports stale data without retrying over it.
- Requests send `User-Agent: helpaffe/<version> (<os>/<arch>)` and validate the
  server's `Helpaffe-Version` header.
- Plain HTTP is accepted on loopback only unless `--insecure-http` or
  `HELPAFFE_INSECURE_HTTP=1` is explicitly set.

## Text input

Short, single-line values use ordinary flags. Multiline or potentially long
content always has an explicit file flag, such as `--message-file`,
`--note-file`, or `--instructions-file`. A path reads UTF-8
from that file and `-` reads stdin. Stdin is never consumed implicitly. Supplying
both an inline value and its file variant is a usage error. Input is preserved
without hard wrapping and normalized to LF line endings.

This makes agent calls reviewable and avoids shell quoting for customer replies
and templates:

```sh
helpaffe ticket reply HLP-42 --version 7 --message-file - <<'EOF'
Thanks for the details. The fix is available now.
EOF
```

SMTP passwords have no inline CLI flag. `project email settings set` accepts
only `--smtp-password-file`; omitting it preserves an already configured
password. Template text and HTML accept explicit inline or file inputs. Email
configuration commands require an administrator agent and the server enforces
that role on every request.

## Exit codes

Exit codes are derived from HTTP status and problem code:

| Exit | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unexpected server response, parse failure, or CLI defect |
| 2 | Invalid invocation or missing local configuration |
| 3 | Resource not found or outside visible scope |
| 4 | Validation or domain refusal that is not a conflict |
| 5 | Conflict, including idempotency mismatch |
| 6 | Stale ticket version |
| 7 | Unauthenticated or forbidden |
| 8 | No eligible ticket for a `next` operation |
| 9 | Client/server version skew |
| 10 | DNS, connection, timeout, or TLS failure |

The server remains the source of permission and domain decisions. The CLI may
reject malformed local arguments, but it does not duplicate role matrices or
ticket transition rules.
