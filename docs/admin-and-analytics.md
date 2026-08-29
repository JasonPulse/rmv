# Admin and analytics

## Authorisation, not just authentication

Discord sign-in proves someone has a Discord account. That is not a
qualification for editing the site. Admin comes from one of two places.

**Root admins, from configuration.**

```
Admin__DiscordIds=123456789012345678,987654321098765432
```

Comma, space, semicolon or newline separated. These cannot be revoked from inside
the app, and they are checked **before** the database is touched, so a root admin
can still get in when Postgres is unreachable or when a grant has gone wrong.
That is what stops you locking yourself out of your own site.

**Granted admins, from the database.** Any existing admin can promote someone at
`/admin/members`. A `Member` row is created the first time each person signs in,
so the page lists real people rather than asking for snowflakes to be typed in.

### One question, one place that answers it

There are two authorities on access and both exist on purpose:

1. `Admin:DiscordIds` in configuration, checked **first**, so a root admin works
   when Postgres is unreachable.
2. `Member.Status` and `Member.IsAdmin` in the database, which is what admins edit.

The authorization policy folds both, in that order. **Nothing else may fold them.**
Every page and service asks the policy through `IAuthorizationService` or an
`[Authorize]` attribute, and `Member.CanContribute` and `Member.CanAdminister` are
read by exactly two lines, both inside `AdminPolicy`, where they are the policy's own
predicate.

That rule is the fix for a real bug rather than tidiness. Three places used to fold
the two authorities by hand:

  The gallery read `Member.CanContribute` to decide whether to offer an upload, and
  `Member.CanAdminister` to decide whether to offer removing someone else's
  screenshot. Both ignore configuration.

  `GalleryService.RemoveAsync` read `Member.CanAdminister` itself, so the service
  was answering an authorization question rather than being told the answer.

  The profile page wrote `IsRoot || Record.CanContribute`, folding configuration and
  the row by hand. That `IsRoot ||` was the tell.

A root admin's rights come from configuration, so a row that had not caught up made
all three say no while every policy said yes: no upload button, no removing anyone
else's screenshot, and `PENDING` next to `ROOT` in the members table.

`GalleryService.RemoveAsync` now takes `mayRemoveAny` from the caller. The gallery
and the profile ask the policy. Configuration is still read directly in exactly one
place for display, to label somebody `ROOT`, where it decides nothing.

### A root admin's row matches the configuration

Root admins come from `Admin:DiscordIds` and the policies check that list before
they touch the database, which is what stops a bad grant or an outage locking you
out. An earlier version reasoned from that: a backfilled row could stay `Pending`
because it granted nothing anyway.

That was only true of the policies. Half the site asks the **row**, not the policy.
The gallery decides whether to offer an upload from `Member.CanContribute` and
whether to offer removing someone else's screenshot from `Member.CanAdminister`, so
a root admin with a `Pending` row was shown neither while passing every policy, and
`/admin/members` read "PENDING" next to "ROOT".

`MemberDirectory.EnsureAsync` now brings the row into line on every access, not only
at creation, so a row that predates the fix corrects itself the next time its owner
loads a page. It writes only when something is wrong.

Blocking a root admin is not something this application can do, and the row says so
rather than pretending otherwise: the configured ids are checked before the
database, so a `Blocked` row was already ineffective. An approver recorded by a real
admin at `/admin/members` is left alone; only a missing one is filled in with
`Admin:DiscordIds`.

## Signing in is not membership

A new sign-in lands on `Pending`. They get a seat and nothing else: they can look
around, and they cannot add or claim anything. An admin approves them at
`/admin/members`, which is what stops a stranger signing in with Discord and
adding characters.

| Status | Can look | Can contribute |
|---|---|---|
| `Pending` | yes | no |
| `Approved` | yes | yes |
| `Blocked` | yes | no |

Two policies, so the distinction is enforced rather than remembered:
`AdminPolicy.Name` for editing the site, `MemberPolicy.Approved` for
contributing. Blocked beats admin in both, so revoking someone does not depend on
remembering to clear the admin flag as well. Promoting someone to admin also
approves them, since an admin who cannot add a character is nonsense.

Blocked rows are kept rather than deleted, or the person could re-register simply
by signing in again.

### Alias, not Discord name

Discord names are whoever got there first, so they rarely match the name people
know each other by in game. Every member has a member-editable `Alias`, set on
their own profile, and `Member.Handle` resolves to the alias with the Discord name
only as a fallback.

`Handle` is what appears on public pages. It is never the Discord id, which is
deliberate: roster pages are public and an account identifier should not leak
through a display name. The admin members list shows both, since knowing which
Discord account an alias belongs to is the point there.

### The row is created on access, not only at sign-in

A sign-in hook records the member, but that is not sufficient on its own. Sessions
outlive deployments now that the Data Protection key ring is in Postgres, so a
perfectly valid cookie can predate the hook or come from a sign-in where it
failed. `MemberDirectory.EnsureAsync` therefore finds or creates, which makes the
row a consequence of *being* signed in rather than of having signed in at the
right moment.

It creates as `Pending`. Backfilling a row must not grant anything, and a root
admin's access comes from configuration regardless.

### Sign-out forms must not set `action`

`<form method="post" action="/Account/Logout">` gets **no antiforgery token**. The
form tag helper only injects one when it owns the URL, so writing the action by
hand silently opts out and every submit is a 400. Use `asp-page` instead:

```html
<form method="post" asp-page="/Account/Logout">
```

Verified by rendering all three shapes: explicit `action` had no token, `asp-page`
and no-action both did.

Two guards:

- You cannot remove your own admin access. It is the one mistake with no route
  back from inside the app.
- Root admins show as `root` and have no revoke action, because the flag would
  have no effect on them anyway.

Applied by convention in `Program.cs`:

```csharp
o.Conventions.AuthorizePage("/Status", AdminPolicy.Name);
o.Conventions.AuthorizeFolder("/Admin", AdminPolicy.Name);
```

**It fails closed.** With no config ids and no database admin, the policy denies
everyone including signed-in users. An empty allowlist must not mean open access,
and a database outage must not grant access either.

Admin links in the account menu render from the policy result, not from whether
any admin exists, so a signed-in non-admin sees only Profile and Sign out.

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

### /admin/path, and where a request came from

The counts answer "what is being hit" and stop there. The question that actually
came up was different: requests for a DAoC signature generator that has been gone
for ten years are still arriving, for character names that belong to this guild,
and where are they coming from.

Every path on the overview links to `/admin/path?p=...`, matched exactly including
the query string, because `?chars=property` is the interesting part. It shows hits,
bot share, first and last seen, status breakdown, referring domains, user agents,
countries, and the last forty hits individually.

**Which signal answers it depends on the caller.** A browser loading an `img`
embedded in a forum post sends that page as `Referer`, so the domain is the whole
answer. A crawler replaying a URL it indexed years ago sends none, and then the
user agent is the answer. Nulls are shown as `(none)` rather than dropped: for a
ten year old URL, "every one of these arrived with no referrer" is the finding, not
an absence of data.

`RequestLog.ReferrerHost` holds the domain on its own. Stored rather than derived
at read time so grouping is an indexed `GROUP BY` instead of parsing 400 characters
of URL per row, and because the domain alone was what was asked for. Only absolute
http and https referrers produce one; a relative or `javascript:` value is a client
making something up, not a site.

The migration backfills it from existing `Referrer` values, which is the point: the
rows that raised the question were already in the table, so filling in only future
traffic would answer the wrong question. Its regex has to agree with
`RequestLogMiddleware.HostOf`, and `ReferrerHostTests` pins that agreement,
including on an over-long host where an unanchored pattern would have stored 253
characters of something that was never a domain.

### Grouped queries

Every grouped query projects an anonymous type and maps to a record afterwards.
Projecting straight into a record constructor inside a `GroupBy` is not
translatable by EF Core and throws at runtime rather than compile time. It looks
fine until the page 500s.

That shape lives once, in `RequestLogQueries`, which both analytics pages use.
Four panels on the overview and three on the path page had their own copies, which
was four then seven chances to forget it.

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
