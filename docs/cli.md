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

Commands follow `helpaffe <object> <verb>`, with singular domain names. Global
commands include `login`, `logout`, `status`, `me`, and `version`. Aliases are
added only after observed use justifies supporting them indefinitely.

## Authentication and configuration

A person runs:

```sh
helpaffe login --url https://support.example.com
```

The CLI uses a device-code flow and stores the resulting user token in the
operating system keychain. If no keychain is available, it writes no secret and
names the explicit alternatives: `HELPAFFE_TOKEN` or
`helpaffe login --token-file PATH`, where the file is created with mode `0600`
and refused if it is readable by others.

An agent does not run `login`; its harness supplies its named credential in
`HELPAFFE_TOKEN`. Credential and instance resolution are deterministic:

1. Instance: `--url`, then `HELPAFFE_URL`, then the last device-login instance.
2. Token: `HELPAFFE_TOKEN`, then an explicitly configured token file, then the
   keychain.

`helpaffe status` prints the selected sources without printing a secret. The
configuration file contains the instance and optional token-file path only. It
defaults to `$XDG_CONFIG_HOME/helpaffe/config.json` or
`~/.config/helpaffe/config.json` and can be overridden by `HELPAFFE_CONFIG`.

## Agent-facing behavior

- Commands never prompt, open an editor, or start a pager. Device login waits
  after printing its code but does not read stdin.
- Domain data goes to stdout. Diagnostics and errors go to stderr.
- `--json` writes exactly one JSON value on stdout on success. On failure it
  writes the problem document as one JSON value on stderr and leaves stdout
  empty.
- Human-readable output is stable enough to read, but scripts depend only on
  `--json` and exit codes.
- Every write gets a UUID idempotency key per invocation. Retries after a lost
  connection reuse the same key.
- Ticket writes require `--version N`; the client sends `If-Match: "N"` and
  reports stale data without retrying over it.
- Requests send `User-Agent: helpaffe/<version> (<os>/<arch>)` and validate the
  server's `Helpaffe-Version` header.
- Plain HTTP is accepted on loopback only unless `--insecure-http` or
  `HELPAFFE_INSECURE_HTTP=1` is explicitly set.

## Text input

Short, single-line values use ordinary flags. Multiline or potentially long
content always has an explicit file flag, such as `--message-file`,
`--note-file`, `--instructions-file`, or `--template-file`. A path reads UTF-8
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
