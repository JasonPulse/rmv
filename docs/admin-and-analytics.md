# Admin and analytics

## Authorisation, not just authentication

Discord sign-in proves someone has a Discord account. That is not a
qualification for editing the site, so admin pages require the caller's Discord
user id to appear in an allowlist:

```
Admin__DiscordIds=123456789012345678,987654321098765432
```

Comma, space or newline separated. Applied by convention in `Program.cs`:

```csharp
o.Conventions.AuthorizePage("/Status", AdminPolicy.Name);
o.Conventions.AuthorizeFolder("/Admin", AdminPolicy.Name);
```

**It fails closed.** With no ids configured the policy denies everyone, including
signed-in users. An empty allowlist must not mean open access.

Admin pages are open in Development so they can be used before Discord exists.

### Turning on Discord sign-in

1. https://discord.com/developers/applications, New Application.
2. OAuth2, add the redirect URI exactly:
   `https://resultsmayvary.org/signin-discord`
   Add a second for `https://www.resultsmayvary.org/signin-discord` if both
   hostnames serve the site.
3. Copy the client id and client secret into the `rmv-app` secret as
   `Discord__ClientId` and `Discord__ClientSecret`.
4. Get your own Discord user id: in Discord, Settings, Advanced, turn on
   Developer Mode, then right-click your name and Copy User ID. Put it in
   `Admin__DiscordIds`.

Until step 4, sign-in works but no admin page does.

**If the button does not appear**, check for a 0-byte `Discord__ClientId` or
`Discord__ClientSecret` in the mounted Secret. Non-empty secret files win over
environment variables by design; empty ones are now ignored, but an old image
would have let them mask a good env var.

## /admin/history

Edits the "where we've been" list behind `/history`. Two things are managed here:

**Games.** Name, guild tags, optional period, active flag, sort order. Guild tags
are one comma-separated field, because that is how the list reads and how it is
edited; each tag renders as its own chip.

**Links.** Several per game, each with a kind (herald, guild, character, stats,
official, other), a label and a URL. The kind picks the small glyph shown before
the label. This is where the character lookup will hang when that lands.

Seeded with the four games the guild started from plus the Uthgard herald, so the
page has content the moment it deploys.

### Game names are type, not logos

Deliberate. The logos are trademarks, and setting each name as a wordmark on the
kit's stone plaque keeps the page visually consistent with everything else.

### URLs are validated by scheme, not sanitised

This is the only place the site puts operator-supplied text into an attribute the
browser acts on. Razor escapes the value, which stops it breaking out of the
attribute, but escaping does nothing about the scheme:

```html
href="javascript:alert(1)"
```

is well-formed HTML and still executes. So `ExternalUrl.TryParse` checks the
scheme against an allowlist of `http` and `https`, requires an absolute URI with
a host, caps the length, and stores the normalised form. `javascript:`, `data:`,
`vbscript:` and `file:` are all rejected at save time and never reach the
database. Covered by tests.

Links render with `target="_blank"` and `rel="noopener noreferrer external"`.

## /admin/analytics

The server-side half. Middleware records every request that reaches routing:
path with query, status, duration, referrer, user agent, and country from
Cloudflare's `CF-IPCountry` header.

**What this sees that a JavaScript beacon cannot:** 404s, redirects, scanner
probes, and anything that never executes JavaScript. "Which URLs are people
trying to hit" is a question only the server can answer, which is why the
Not Found panel includes bots by default.

Deliberately holds **no IP address and no cookie**. Country is coarse enough not
to identify anyone, so there is nothing here needing a consent banner.

### Two things worth knowing

`UseStatusCodePagesWithReExecute` runs the pipeline a second time to render
`/Error`, which reaches the middleware again. Without a guard, every 404 records
twice: once as the path the visitor asked for and once as `/Error?code=404`. The
middleware skips requests carrying `IStatusCodeReExecuteFeature`.

Writes are batched on a background loop, not on the request path. The queue is
bounded at 10k and drops on overflow: losing analytics rows under load is always
better than slowing down the actual request. Static assets and `/healthz/*` are
skipped, since kubelet alone would otherwise be most of the table.

### Grouped queries

Every grouped query projects an anonymous type and maps to a record afterwards.
Projecting straight into a record constructor inside a `GroupBy` is not
translatable by EF Core and throws at runtime rather than compile time. It looks
fine until the page 500s.

## Umami

The client-side half, `deploy/k8s/umami.yaml`. It answers a different question:
sessions, devices, screen sizes, journeys. Needs its own database on the existing
Postgres instance; the manifest header has the SQL and the secret command.

The tracking snippet renders only when both values are set, so the site loads no
third-party script by default:

```
Analytics__UmamiScriptUrl=https://stats.example.org/script.js
Analytics__UmamiWebsiteId=<from the Umami dashboard>
```

Put Umami behind its own tunnel hostname with a Cloudflare Access policy, or
keep it reachable only on your tailnet. It is an admin surface.

## Retention

Nothing prunes `request_logs` yet. At this site's traffic that is fine for a long
while, but it grows without bound. When it matters, a nightly delete of rows
older than 90 days is the whole job.
