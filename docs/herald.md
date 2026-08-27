# Heralds

Character data comes from each server's herald. A player types a character name;
the server fetches it. Admins set which herald belongs to which game.

## Why a code adapter per server

Because no two heralds are the same kind of thing:

| Server | Shape |
|---|---|
| Blackthorn DAoC | server-rendered HTML, per-character pages |
| HeraldXI (FFXI) | JSON API at `/api/v1`, internal only |
| WoW Armory | client-rendered JS, nothing to parse |

A configurable table mapping was considered and would have covered Blackthorn
alone. It cannot express a JSON API, and it cannot help at all with a page whose
HTML contains no data.

## Fetching is the security-critical part

An admin types a URL and the **server** fetches it. That makes the app an
attacker-influenced HTTP client, which is the classic SSRF shape: `http://10.0.0.5/`
or `http://169.254.169.254/` turns the pod into a proxy for whatever the cluster
can reach.

**Validating the URL string is not the control.** A well-formed hostname can
resolve to a private address, and a hostname can resolve somewhere public when an
admin saves it and somewhere internal by the time it is fetched.

The control is `HeraldHttpHandler`'s connect callback:

1. Resolve the host.
2. Drop loopback, RFC1918, link-local, carrier-NAT, benchmarking, multicast, and
   the v6 equivalents, including v4-mapped-in-v6.
3. Connect to an **already-vetted address**, not to the hostname, so nothing can
   re-resolve in between.

Every redirect hop goes through it too, because each hop opens a connection.
Extracted from `Program.cs` specifically so it can be tested: a correct policy
behind a handler nobody wired protects nothing.

## The adapter owns its address

An admin picks a herald from a dropdown and that is the whole configuration. The
address comes from the adapter's `DefaultBaseUrl`.

This was originally a field an admin filled in, which was wrong on its face: an
adapter is code written against one server's markup or API, so it cannot work
against a different address. Making it typeable added a way to get it wrong and
nothing else, and it duly went wrong: a URL entered without also choosing an
adapter was silently discarded, so a game looked configured and was not.

Each game keeps an optional override, tucked behind a disclosure, for the day a
server changes domain. Blank is the normal state.

## The allowlist, and why it lives in configuration

The FFXI herald is internal. `heraldxi.network-gnomes.com` resolves publicly to
`172.25.75.70`, which is RFC1918, so the address check alone would refuse a
herald that is meant to work.

```
Herald__AllowedPrivateHosts=heraldxi.network-gnomes.com
```

Comma, space or newline separated. A listed host skips the address check, because
an operator has taken responsibility for it by name.

**This is deliberately not editable from the admin UI.** A web admin can point a
game at any public herald; permitting an internal address takes a deployment
change. If admins could allowlist hosts themselves, the SSRF guard would be
bypassable by exactly the people it constrains.

Without the variable set, FFXI fetches fail with a message saying the host is not
public and not allowlisted. That is correct behaviour, not a bug.

## Limits

2MB response cap, checked while reading rather than trusting `Content-Length`.
15 second overall timeout, 8 second connect timeout, 3 redirects. Responses are
decoded as UTF-8 with replacement, because heralds are often not valid UTF-8 and
a stray byte should not throw.

## Errors a player will see

Both heralds answer 404 for a name they do not know, so adapters turn that into
"The herald has no character called X" rather than reporting a status code at
someone who mistyped. Blackthorn also answers 200 with a shell in some cases, so
its adapter checks the page title as well.

## Names are validated before they reach a URL

DAoC and FFXI names are single alphabetic words, so that is what the adapters
accept: `[A-Za-z]`, capped at 24 and 16. Path traversal, query injection and
markup cannot get into the request because they are not names.

## WoW

The armory is a JS shell: 20KB of HTML, zero tables, 535 characters of visible
text. Scraping it needs a headless browser in the pod and breaks on every
redesign.

The real route is Blizzard's Battle.net API, which is free and returns JSON, but
needs an OAuth client created at develop.battle.net and its credentials in
configuration. Not built. WoW is also a no-longer-active game on the history
page, so its characters are historical.

## Adding a character

A member goes to `/characters`, picks a game and types a name. Only games with an
adapter *and* a base URL appear in the list: offering the rest just produces a
failure after the fact.

Nothing is saved unless the herald confirms the character, so a typo leaves no
row behind. The name stored is the herald's echo of it, not what was typed, so
capitalisation matches the game.

**One character belongs to one member**, enforced by a unique index on
`(game, name)` rather than only in the handler, so a double submit cannot create
two owners. A second claim says who already has it. Comparison is
case-insensitive, because "Arwen" and "arwen" are the same character.

Adding requires the approved policy, not merely a sign-in. Rate limited to 10 per
five minutes per caller, harder than the upload limit, because the cost lands on
a server that is not ours.

A refresh keeps the previous stats when the herald fails. A herald being down
should not blank a character that was fine yesterday; `LastError` records why and
`LastFetchedAt` says how stale it is.

## Testing

Adapters are tested against real saved responses in
`tests/Rmv.Web.Tests/Fixtures`, not hand-written markup, because hand-written
markup only proves the parser matches my idea of the page.

Three suites, and the split matters:

| Filter | What it uses | In CI |
|---|---|---|
| default | saved fixtures | yes |
| `Category=Database` | real Postgres, fake herald | no |
| `Category=Network` | real heralds | no |

```bash
dotnet test --filter Category=Database    # needs RMV_TEST_POSTGRES
dotnet test --filter Category=Network
```

The service tests use a **fake adapter on purpose**. An earlier version hit
Blackthorn eight times per run, and after a few runs in a minute results started
failing. That looked like a bug in the service and was my own suite being rude to
someone else's server. Only the parsing needs real markup, and saved fixtures
cover that; the live tests are four requests, run deliberately.

A test that passes on retry is worse than one that fails, because it teaches you
to rerun instead of to look.
