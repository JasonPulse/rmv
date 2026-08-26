# Deployment requirements

Production runs on Kubernetes. `deploy/k8s` holds a kustomize base; this file is
the requirements behind it.

Two workloads are deployed. Postgres already exists and is reached over the
network by connection string.

```
internet -> Cloudflare edge -> [cloudflared] -> Service/web -> [web] -> existing Postgres
```

## Image

`ghcr.io/jasonpulse/rmv` — multi-arch manifest list, **linux/amd64 and
linux/arm64**.

The build stage is pinned to `BUILDPLATFORM` and cross-publishes via
`dotnet publish -a $TARGETARCH`, and the runtime stage has no `RUN` steps, so
neither architecture needs QEMU emulation. Both build in one pass.

Runs as **uid/gid 1654** (`app`, the base image's `APP_UID`, verified against the
image). Listens on **8080/tcp**, HTTP only, no TLS in the container.

Not published yet: CI triggers on `main` and the repo branch is `master`.

## Ingress

Cloudflare tunnel. No Ingress resource, no LoadBalancer, no inbound ports.

- Tunnel public hostname to `http://<web-service>:80`
- Service type HTTP, TLS verification off; that hop is inside the cluster and
  the public side is still HTTPS at the edge
- The tunnel token is a credential

Two cloudflared replicas, so a rollout or node drain does not drop the tunnel.
Cloudflare load-balances across connectors registered to the same tunnel.

The app runs `UseForwardedHeaders` with `KnownProxies` and `KnownIPNetworks`
cleared, because cloudflared's pod address is not stable. It therefore trusts
`X-Forwarded-*` from anything that can reach it. **The web port must not be
reachable from outside the cluster.** If it ever is, pin the proxy address in
`Program.cs` first.

cloudflared has no file-based option for its token, so that one credential does
reach the pod environment via `secretKeyRef`. Use a credentials-file tunnel
instead if that is unacceptable.

## Configuration

ASP.NET Core, so `__` in a name is a `:` in configuration. Any value can be an
environment variable **or** a file: the app reads `/run/secrets` through
`AddKeyPerFile`, where each filename is the config key.

Secret, supplied as mounted files:

| Key | Required | Notes |
|---|---|---|
| `ConnectionStrings__Postgres` | no | Npgsql connection string. Absent, the site runs and reports the database as not configured |
| `Discord__ClientId` | no | both must be non-empty or sign-in stays hidden |
| `Discord__ClientSecret` | no | as above |

Non-secret environment:

| Variable | Value | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` exposes `/status` with no sign-in |
| `ASPNETCORE_HTTP_PORTS` | `8080` | set in the image |
| `DOTNET_gcServer` | `0` | set in the image |
| `Build__Version` | git sha | from the `BUILD_VERSION` build arg, shown on `/status` |

**Mount requirement.** Secret files must be readable by gid 1654. Kubernetes owns
them as root, so this needs `fsGroup: 1654` and mode `0440`. With `0400` the app
dies at startup with `UnauthorizedAccessException` on `/run/secrets/...` and
crash-loops. This was found by applying the manifests, not by reading them.

## Postgres (existing instance)

Nothing deploys a database. The app connects out to the instance already running.

**Version.** Any currently supported release. The floor is PostgreSQL 10, because
the schema uses identity columns. No extensions needed.

**What it needs.**

- A database, or an existing one it can share. The app does not qualify a schema,
  so it uses whatever the role's `search_path` resolves to, normally `public`.
- A role with `CONNECT` on the database and `CREATE` on the schema, plus the usual
  DML. Not superuser.
- `CREATE` is required because the app applies its own migrations at startup.
  There is no init job and no migration hook.

**What it creates.** Three tables and one index, nothing else:

| Object | Purpose |
|---|---|
| `__EFMigrationsHistory` | EF Core migration bookkeeping |
| `deployments` | one row per pod start; history, safe to truncate |
| `data_protection_keys` | shared key ring; **never truncate**, it invalidates every session |
| `IX_deployments_StartedAt` | index on `deployments` |

`deploy/schema.sql` is the idempotent DDL the app will run, generated from the
migrations. Give it to whoever owns the instance if they would rather pre-create
the schema and then withhold `CREATE` from the role. Run that script rather than
hand-writing equivalent DDL, or the migration history will not match and the app
will try again.

**Connection string.** Standard Npgsql, in the `ConnectionStrings__Postgres`
secret key:

```
Host=HOST;Port=5432;Database=DB;Username=USER;Password=PW;Maximum Pool Size=10
```

- Add `SSL Mode=Require` if the instance demands TLS, and
  `Trust Server Certificate=true` only if it presents a self-signed certificate.
- **Set `Maximum Pool Size` explicitly.** Npgsql defaults to 100 connections per
  process, so two replicas can open 200 against a shared instance. This site is
  low traffic and 10 per pod is generous.
- `EnableRetryOnFailure` is already on in the app, so transient drops are retried
  without configuration.

**Failure behaviour.** If the instance is unreachable the pods do not crash-loop.
They serve pages, surface the error on `/status`, hold `/healthz/ready` at 503,
and retry with backoff. They recover with no restart when it returns. Nothing on
a public page reads the database.

## Pod requirements

- Non-root, uid/gid 1654
- `readOnlyRootFilesystem` is fine, but **/tmp must be writable** (emptyDir).
  Without it .NET fails to start
- No service account token needed
- All capabilities droppable
- ~50m CPU and 128Mi memory request, 512Mi limit, is comfortable

## Health endpoints

| Path | Checks | Use for |
|---|---|---|
| `/healthz/live` | the process is serving | liveness, readiness, startup |
| `/healthz/ready` | also Postgres | alerting only |

**Do not gate traffic on `/healthz/ready`.** No public page reads the database, so
a Postgres outage must not remove pods from the Service or restart them.

Expect `/healthz/ready` to return 503 for a few seconds on first start while
migrations apply.

## Replicas

The web deployment scales horizontally. The ASP.NET Data Protection key ring is
persisted to Postgres, so sign-in cookies validate across pods and survive
redeploys.

- Multi-replica sign-in **requires** the database. Without it, keys are
  per-process and cookies break on every restart and across pods.
- Never truncate `data_protection_keys`.

## Discord sign-in

Optional; the site runs without it. Redirect URI must be exactly
`https://YOUR-DOMAIN/signin-discord`. Both the client id and secret must be
non-empty or sign-in stays hidden.

## /status

Build sha, hostname, boot count and database state. Requires sign-in in
Production and is open in Development, so until Discord is wired use
`/healthz/ready` and the pod logs.

## Content

`./content` (markdown) can be mounted read-only at `/app/content`, so a post is a
file copy rather than a rebuild. Nothing reads it yet; that lands with the news
section.
