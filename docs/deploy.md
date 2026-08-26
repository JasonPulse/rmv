# Deploying to the homelab

Three containers behind a Cloudflare tunnel. Only the tunnel talks to the
internet, and it does so with outbound connections only, so there are no
published ports and no inbound firewall holes.

```
internet -> Cloudflare edge -> [tunnel] --network--> [web] --network--> [db]
```

## Every environment variable

The web container is ASP.NET Core, so `__` in a variable name is a `:` in its
configuration. `Discord__ClientId` sets `Discord:ClientId`.

### web

| Variable | Required | Secret |
|---|---|---|
| `ConnectionStrings__Postgres` | no | yes |
| `Discord__ClientId` | no | no |
| `Discord__ClientSecret` | no | yes |

- `ConnectionStrings__Postgres` is a full Npgsql connection string. Unset, the
  site still runs and reports the database as not configured.
- Both Discord values must be non-empty for sign-in to appear. Either blank
  leaves it switched off, which is a supported state.

Set by the image, override only with reason:

| Variable | Value | Why |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` opens `/status` with no sign-in |
| `ASPNETCORE_HTTP_PORTS` | `8080` | the port the tunnel targets |
| `DOTNET_gcServer` | `0` | workstation GC, lower idle memory |
| `Build__Version` | git sha | from the `BUILD_VERSION` build arg, shown on `/status` |

### db (`postgres:18-alpine`)

| Variable | Required | Secret |
|---|---|---|
| `POSTGRES_DB` | no, defaults to `rmv` | no |
| `POSTGRES_USER` | no, defaults to `rmv` | no |
| `POSTGRES_PASSWORD` | one of these two | yes |
| `POSTGRES_PASSWORD_FILE` | one of these two | path only |

Setting both makes the image refuse to start. Setting neither makes it exit
saying the superuser password is not specified.

### tunnel (`cloudflare/cloudflared`)

| Variable | Required | Secret |
|---|---|---|
| `TUNNEL_TOKEN` | yes | yes |

### Compose interpolation, read from `.env`

These are consumed by `docker-compose.yml` itself, not by any container:

| Variable | Purpose |
|---|---|
| `POSTGRES_DB` `POSTGRES_USER` `POSTGRES_PASSWORD` | passed to db, and built into the app's connection string |
| `DISCORD_CLIENT_ID` `DISCORD_CLIENT_SECRET` | passed to web |
| `CLOUDFLARE_TUNNEL_TOKEN` | passed to tunnel |
| `BUILD_VERSION` | build arg, stamped into the image |
| `WEB_IMAGE` | run a prebuilt image instead of building locally |

## Credentials: two modes

### Mode A, `.env` file

Simplest. Copy `.env.example` to `.env`, fill it in, `chmod 600 .env`.

```bash
docker compose up -d
```

Credentials end up in the container environment, so they are visible to anything
that can run `docker inspect`.

### Mode B, file-based secrets (preferred)

`docker-compose.secrets.yml` mounts each credential as a file under
`/run/secrets`. The app reads that directory through `AddKeyPerFile` in
`Program.cs`, so nothing sensitive appears in compose, in `docker inspect`, or in
the process environment.

Each secret's *filename* is the config key, with `__` for the `:`.

```bash
mkdir -p secrets && chmod 700 secrets
printf '%s' 'Host=db;Port=5432;Database=rmv;Username=rmv;Password=REAL' \
  > secrets/ConnectionStrings__Postgres
printf '%s' 'REAL'   > secrets/postgres_password
printf '%s' 'ID'     > secrets/Discord__ClientId
printf '%s' 'SECRET' > secrets/Discord__ClientSecret
chmod 600 secrets/*

docker compose -f docker-compose.yml -f docker-compose.secrets.yml up -d
```

The overlay removes the matching environment variables with `!reset null`. A
bare `KEY:` would not do it: Compose reads that as "inherit from the host
environment", which is how the Postgres both-are-set error first appeared.

`secrets/` is gitignored. `.env` still needs `POSTGRES_DB` and `POSTGRES_USER`
in this mode; neither is sensitive. Compose warns that `POSTGRES_PASSWORD` is
unset, which is expected here and harmless.

Swarm or an external secrets manager: change each `file:` to `external: true`
and create the secrets out of band. Nothing else changes.

A trailing newline in a secret file is harmless. Both .NET's KeyPerFile provider
and the Postgres entrypoint strip trailing whitespace; tested both ways.

## Cloudflare tunnel

In the Zero Trust dashboard, Networks then Tunnels:

1. Create a tunnel, choose Docker, copy the token into `CLOUDFLARE_TUNNEL_TOKEN`.
2. Add a public hostname on that tunnel:
   - Service type `HTTP`
   - URL `web:8080` (the compose service name, not localhost or an IP)
3. Leave TLS verification off. The hop from the edge to `web` is inside the
   compose network; the public side is still HTTPS at the edge.

The token is the tunnel's full credential. Rotate it by deleting the tunnel and
creating a new one.

`cloudflared` waits on the web container's healthcheck, which is
`/healthz/ready`, so it will not route traffic to a site that cannot reach
Postgres yet.

## Discord sign-in

Optional. The site runs without it.

1. https://discord.com/developers/applications, create an application.
2. OAuth2, add the redirect URI exactly: `https://YOUR-DOMAIN/signin-discord`
3. Put the client id and secret in `.env` or in secret files.

The app runs `UseForwardedHeaders`, because requests arrive from `cloudflared`
over plain HTTP. Without it ASP.NET Core sees `scheme=http` and the container
hostname and builds a `redirect_uri` Discord rejects.

`KnownProxies` and `KnownIPNetworks` are deliberately cleared, since
cloudflared's address inside the Docker network is not stable. That trusts
`X-Forwarded-*` from anything that can reach the app, which is safe only because
**the web port is not published**. If you ever publish it, pin the proxy address
in `Program.cs`.

## Running a prebuilt image

CI publishes to `ghcr.io/<owner>/<repo>` on every push to the default branch.

```bash
echo 'WEB_IMAGE=ghcr.io/jasonpulse/rmv:latest' >> .env
docker compose pull web && docker compose up -d web
```

Without `WEB_IMAGE`, compose builds from source on the box.

## Verifying a deployment

From the host:

```bash
docker compose ps                      # all three up, db and web healthy
docker compose logs -f web
```

`web` publishes no port, so check it from inside the network:

```bash
docker compose exec web curl -fsS localhost:8080/healthz/ready   # Healthy
docker compose exec web curl -fsS -o /dev/null -w '%{http_code}\n' localhost:8080/
```

Then from outside, against the real hostname: `/` returns 200, `/healthz/live`
returns 200, `/healthz/ready` returns 200 once migrations have applied.

`/status` has the build sha, hostname, boot count and database state, but it
requires sign-in in Production. Until Discord is wired, use `/healthz/ready` and
the container logs.

## What to expect on first start

Migrations run in a background service, not during startup, so the site comes up
immediately and `/healthz/ready` returns 503 until the schema is applied. That is
normal for the first few seconds.

If Postgres is unreachable, the web container does **not** crash-loop. It serves
pages, reports the error on `/status`, keeps `/healthz/ready` at 503, and retries
with backoff. It recovers without a restart when the database returns.

One row is written to `deployments` per boot. That table is the deployment
history and is safe to truncate.

## Backups

Everything stateful is the `pgdata` volume, which holds `/var/lib/postgresql`.

```bash
docker compose exec -T db pg_dump -U rmv rmv | gzip > rmv-$(date +%F).sql.gz
```

Note the volume mounts at `/var/lib/postgresql`, not `/var/lib/postgresql/data`.
Postgres 18 images place the cluster in a major-version subdirectory so
`pg_upgrade --link` works across the mount, and mounting the old path makes the
container refuse to start.

## Content

`./content` is mounted read-only into the web container, so a markdown post is a
file copy on the host rather than a rebuild. Nothing renders it yet; that lands
with the news section.
