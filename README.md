# Results May Vary

Guild website. ASP.NET Core 10 Razor Pages, Postgres, Discord sign-in, deployed
on a homelab behind a Cloudflare tunnel.

## Why this stack

Razor Pages, not Blazor. A request arrives, `OnGet()` runs, it queries Postgres,
the template renders. Same per-request lifecycle as PHP, no client runtime, no
websocket circuit, no build step. Blazor Server holds a SignalR connection per
visitor and round-trips a network call for a button click, which is a poor fit
for a public site behind a tunnel.

htmx handles the interactive bits. One script tag, and updating part of a page
is an attribute rather than a JavaScript file:

```html
<button hx-get="/?handler=Status" hx-target="#status-panel" hx-swap="outerHTML">
```

## Running it locally

Needs Docker. The .NET SDK is only needed to open the project in Rider.

```bash
cp .env.example .env          # then set POSTGRES_PASSWORD
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build
```

The site comes up on http://localhost:5080. The local overlay publishes that
port and switches the tunnel off.

To run the app from Rider or `dotnet watch` with only Postgres in Docker, first
install the SDK, then set `POSTGRES_PASSWORD=rmv` in `.env` so it matches
`appsettings.Development.json`:

```bash
brew install --cask dotnet-sdk
docker compose -f docker-compose.yml -f docker-compose.local.yml up -d db
dotnet watch --project src/Rmv.Web
```

The local overlay binds Postgres to `127.0.0.1:5432` so the host can reach it.
`dotnet watch` reloads on save.

## Deploying to the homelab

Full runbook with every environment variable in `docs/deploy.md`. The short
version, credentials in `.env`:

```bash
cp .env.example .env          # fill in all of it
docker compose up -d
```

Or with credentials as mounted files rather than environment variables:

```bash
docker compose -f docker-compose.yml -f docker-compose.secrets.yml up -d
```

Three containers: `web`, `db`, and `tunnel`. Only `tunnel` talks to the outside
world, and only outbound, so there are no published ports and no inbound
firewall holes. Point the tunnel's public hostname at `http://web:8080`.

## Discord sign-in

Sign-in is off until credentials exist, and the site runs fine without it. To
switch it on:

1. https://discord.com/developers/applications, create an application.
2. OAuth2, add the redirect URI `https://YOUR-DOMAIN/signin-discord` exactly.
3. Put the client id and secret in `.env`.

Nothing on the site gates on membership yet. Signing in only proves the wiring.

The app runs `UseForwardedHeaders` because requests arrive from cloudflared over
plain HTTP. Without it, ASP.NET Core sees `scheme=http` and the container
hostname, and builds a `redirect_uri` Discord rejects. `KnownProxies` is
deliberately cleared, which is safe only because the app port is not published.
If you ever expose it directly, pin the proxy address in `Program.cs`.

## Layout

```
src/Rmv.Web/
  Program.cs              startup, auth, proxy headers, health, migrations
  Data/                   DbContext, entities, migrations
  Pages/                  Razor Pages
  wwwroot/img/ui/         assets sliced from the GUI kit
  wwwroot/img/logo/       logo, crest and icons
  wwwroot/css/rmv.css     the theme
  wwwroot/css/_slices.css generated, do not hand-edit
content/                  markdown, mounted read-only in the container
tools/                    asset slicing and measurement scripts
docs/deploy.md            homelab runbook, every environment variable
docs/ui-kit.md            how the theme was built and how to extend it
docs/logo.md              how the logo and icons are generated
```

## /status

Build sha, hostname, boot count and database state. Not for members, so it
requires sign-in in Production and is open in Development. Nothing on the public
pages reads the database, so the home page cannot fail because Postgres is down.

## Health

`/healthz/live` answers as soon as the process is up. `/healthz/ready` also
requires Postgres, and is what Docker's healthcheck uses, so the container is
never marked healthy while the database is still starting.

## Notes

The `deployments` table is the site's proof-of-life: one row per boot, written at
startup and read by the home page. A wrong connection string or an unapplied
migration fails loudly at startup rather than silently on a member's first page.

Two asset pipelines, both rerunnable scripts. `tools/slice-nordic-ui.sh` pulls
frames and textures out of the GUI kit PSD; `tools/build-logo-assets.sh` turns
the guild logo into the hero lockup, the masthead crest and every icon. The
palette is sampled from the logo so the two cannot drift. See `docs/ui-kit.md`
and `docs/logo.md`.

Postgres is optional. With no connection string the site still runs and says so;
with one pointing at a database that is down, the site serves pages, reports the
error, and `/healthz/ready` returns 503 until a background retry succeeds. No
crash loop, and no restart needed when the database comes back.

The solution uses the `.slnx` format that .NET 10 emits by default. Rider 2025.1
and newer opens it.
