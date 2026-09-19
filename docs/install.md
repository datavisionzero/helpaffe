# Install a released helpaffe instance

Release `v0.1.0` provides one application image containing the API and Web UI,
plus PostgreSQL in a two-service Compose stack. The application image supports
`linux/amd64` and `linux/arm64`. No source checkout, .NET SDK, Node.js, or Go
toolchain is required on the server.

## Start

Install Docker Engine with Compose and download both deployment files from the
same release tag:

```sh
mkdir helpaffe && cd helpaffe
base=https://raw.githubusercontent.com/datavisionzero/helpaffe/v0.1.0/deploy
curl -fsSLo compose.yaml "$base/compose.yaml"
curl -fsSLo .env "$base/.env.example"
chmod 600 .env
```

Edit `.env` and set these four required values:

- `POSTGRES_PASSWORD`: a new database password; generate with
  `openssl rand -hex 24`.
- `HELPAFFE_BOOTSTRAP_EMAIL`: the first administrator's real email address.
- `HELPAFFE_BOOTSTRAP_PASSWORD`: a new password of at least 12 characters;
  generate with `openssl rand -base64 24`.
- `HELPAFFE_SECRETS_ENCRYPTION_KEY`: one Base64-encoded 32-byte value;
  generate once with `openssl rand -base64 32`. Keep it with database backups:
  losing it makes stored SMTP passwords unreadable.

The example pins `HELPAFFE_VERSION=0.1.0`. Do not commit or share `.env`.
Start the stack and wait for PostgreSQL, migrations, and the application:

```sh
docker compose up -d --wait
curl --fail http://127.0.0.1:5066/health/ready
```

Open `http://localhost:5066` on the server for a local trial and sign in with
the bootstrap email and password. The bootstrap account is created only when
there are no users. Configure projects, product API keys, named agent
credentials, and each project's SMTP settings in the Web UI. A product API key
belongs in that product's trusted backend, never in browser code.

## Remote access

The published port binds to `127.0.0.1` by default. Put a reverse proxy on
the same host in front of it and terminate TLS there. For example, a host-level
Caddy configuration can contain:

```caddyfile
support.example.com {
    reverse_proxy 127.0.0.1:5066
}
```

Point the domain at the host and make ports 80 and 443 reachable to Caddy.
Do not change `HELPAFFE_BIND_ADDRESS` to `0.0.0.0` for internet access:
that would expose the login and Product API over plain HTTP. Configure each
project's customer and backoffice ticket URLs to use its actual HTTPS address.

## Install the agent CLI

Download the archive for the agent host from the
[v0.1.0 release](https://github.com/datavisionzero/helpaffe/releases/tag/v0.1.0).
The release includes Linux and macOS archives for amd64 and arm64, plus
`SHA256SUMS`. For example, on Linux amd64:

```sh
assets=https://github.com/datavisionzero/helpaffe/releases/download/v0.1.0
curl -fL -O "$assets/helpaffe_0.1.0_linux_amd64.tar.gz"
curl -fL -O "$assets/SHA256SUMS"
grep 'helpaffe_0.1.0_linux_amd64.tar.gz' SHA256SUMS | sha256sum -c -
tar -xzf helpaffe_0.1.0_linux_amd64.tar.gz helpaffe LICENSE
./helpaffe version
```

Use a named agent credential from the Web UI. The CLI and server versions must
match; keep the token in a secret store or a mode-0600 file, not a shell
argument. See [CLI usage](cli.md).

## Upgrade and backup

Before upgrading, stop the application and back up the PostgreSQL database
**and** the attachment volume together. For an installation started from this
directory:

```sh
umask 077
docker compose stop helpaffe
docker compose exec -T db pg_dump -U helpaffe -d helpaffe -Fc > helpaffe.dump
app_container=$(docker compose ps -q --all helpaffe)
docker run --rm --volumes-from "$app_container" \
  alpine:3 tar -cz -C /var/lib/helpaffe/attachments . \
  > helpaffe-attachments.tar.gz
docker compose start helpaffe
```

Keep both files in access-controlled, encrypted storage with the corresponding
`HELPAFFE_SECRETS_ENCRYPTION_KEY`. The
[operations guide](operations.md#database-backup-and-restore) covers database
restore and post-restore checks; when using the downloaded Compose file, run
its commands from this directory without the source-checkout `-f` and
`--env-file` flags. Restoring replaces current data and needs a maintenance
window.

Change `HELPAFFE_VERSION` to the chosen release, then run:

```sh
docker compose pull
docker compose up -d --wait
```

The application applies forward-only migrations at startup. There is no
automatic schema downgrade; a matching backup is the rollback path. Keep the
matching CLI archive when upgrading. `:latest` follows stable releases, but
pinning a version leaves the upgrade decision with the operator.
