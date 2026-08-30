# Working on Results May Vary

ASP.NET Core 10 Razor Pages, htmx, EF Core 10 on Postgres, deployed to Kubernetes.
`src/Rmv.Web` is the site, `tests/Rmv.Web.Tests` is the suite, `tools/smoke.sh` is
the proof.

## The rule that matters most

**One question, one place that answers it. Everything else calls that place.**

Not "avoid copy-paste". The rule is about *questions*, and the dangerous version of
breaking it shares no text at all with itself.

The bug that produced this file: whether someone may administer the site has two
inputs, the root ids in `Admin:DiscordIds` and the member row. Both exist on
purpose. The authorization policies folded them in that order. The gallery and the
profile page read the row on its own. A root admin whose row still said Pending
passed every policy on the site and was shown no upload button, and the admin table
printed "PENDING" next to "ROOT". Three sites folded two authorities by hand and got
three answers.

`AdminPolicy.IsRootAdmin(config, id)` and `Member.CanAdminister` share not one
token, so a duplicate-block detector reports zero. It did. Reading the code found it
in twenty minutes. **Read the code. Do not build a detector.**

Before writing a second place that decides something, look for the first one. The
shapes to be suspicious of:

- A `||` that folds two sources of truth inline. The `IsRoot ||` was the tell.
- A predicate about a domain rule written into a LINQ `Where`. If it is a rule, it
  belongs somewhere with a name.
- A number in a validation attribute that is also a column width, or a limit in a
  service.
- A find-or-create where the create branch and the update branch each map the
  fields.
- A rule expressed once positively and once inverted (`!= Blocked` here,
  `== Blocked` there).

If two things genuinely answer different questions, leave them alone and say so.
Similar shape is not the same question. `AdminPolicy.Parse` and
`HeraldHttpHandler.ParseAllowedPrivateHosts` both split a config list, and they
should stay separate: one is case-sensitive Discord ids, the other is
case-insensitive hostnames.

## Where each question is answered

Change these, not their callers:

- **What may this person do.** `CurrentMember.AccessAsync` returns `Access`, which
  is folded in `Access.Of` and nowhere else. The authorization handlers, the
  masthead, and every page read that one answer. `Member` deliberately has no
  `CanContribute` property.
- **Who appears publicly.** `RosterVisibility`. `Shows(member)` in memory,
  `.OnRoster()` on a query. Was seven copies.
- **Which characters a herald answers for.** `RosterVisibility.FromHerald()`.
- **Which address a game's herald is at.** `HeraldAddress.For(game, adapter)`.
- **Who writes a member row.** `MemberDirectory`, only. `EnsureAsync` on a request,
  `RecordSignInAsync` from the OAuth hook. Program.cs used to upsert rows itself
  with different rules, which is how the Pending root admin got created.
- **Surviving a database outage on a public page.** `PageHelpers.TryLoadAsync`. The
  roster page hand-rolled it without the try and 500'd during a restart.
- **Serving stored image bytes.** `StoredImage` for the ETag, the conditional and
  the cache header. `ScreenshotEndpoint.PathFor` and `PortraitEndpoint.PathFor` for
  the URL, beside the route they must match.
- **Limits.** `CharacterLimits`, `GalleryLimits`, `SpellcraftTemplate.MaxPerMember`,
  `ExternalUrl.MaxLength`, `RequestLogMiddleware.MaxTextLength`. The DbContext, the
  form attributes and the services all read these. Never retype the number.
- **The order games are listed in.** `GameOrder.Listed()` for lists and pickers,
  `GamePresence.NewestFirst` for the history page and the leaderboards.
- **Ranking.** `Leaderboard.Rank` and `Leaderboard.Value`, pure and tested offline.
- **Analytics panels.** `RequestLogQueries.TopAsync`.
- **An external URL becoming an href.** `ExternalUrl.TryParse`. Scheme allowlist,
  because Razor escaping does nothing about `javascript:`.
- **Whether an address may be connected to.** `AddressPolicy.IsAllowed`, enforced in
  `HeraldHttpHandler`'s connect callback, not at save time.
- **Shared rendering.** `_StatusPill`, `_Flash`, `_CharacterBody`. All three exist
  because two pages had drifted copies.

## Verification bar

Never report work as done off a proxy. "It compiles" and "the diff looks right" are
not evidence.

```bash
dotnet test tests/Rmv.Web.Tests --filter "Category!=Database&Category!=Network"
export RMV_TEST_POSTGRES="Host=localhost;Port=5432;Database=rmv_test;Username=rmv;Password=..."
dotnet test tests/Rmv.Web.Tests --filter "Category=Database"
dotnet test tests/Rmv.Web.Tests --filter "Category=Network"   # real heralds, flaky
bash tools/smoke.sh                                # full stack, cold volume
bash tools/smoke.sh https://www.resultsmayvary.org # after deploying
```

`Network` tests hit other people's servers and fail intermittently. Re-run before
believing one.

Proving a fix is real: break it deliberately and watch a named test fail. When the
single fold was reverted, six offline tests failed and named the root-with-Pending
case. Worth knowing: no database test failed, because `MemberDirectory` had already
corrected the row. A test that goes through the whole stack can hide the defect it
is meant to catch. Keep the rule itself covered by a pure test.

## Docker hygiene

There are other people's containers and images on this machine. `xitestsrv`,
`xitestdb`, `xiserver`, `xiherald`, `mariadb*`, `jasonpulse/*` and
`buildx_buildkit_*` must survive. `tools/smoke.sh` tears down only its own compose
project and its own image tag. **Never `docker image prune`.** Never remove
anything by wildcard.

## Releasing

```bash
git push origin master        # master, never main
```

Then wait for CI and check GHCR. The tag is `sha-` plus the **full 40 character
sha**, not the short one:

```bash
git rev-parse HEAD
curl -s "https://api.github.com/repos/JasonPulse/rmv/actions/runs?per_page=3"
```

Then, and only for this one deployment:

```bash
kubectl --context pulse-clift -n homelab rollout restart deploy/rmv
```

There are 50 other clusters on this kubeconfig. Nothing else is yours to restart.

## Traps this codebase has already hit

- `[Authorize]` and `[EnableRateLimiting]` are **ignored** on a Razor Page handler
  method. It is now a build error (MVC1001). Put them on the class, or give the
  action its own page, or check inside the handler.
- A Razor Page with no GET handler still renders. A deleted `OnGetAsync` fails
  silently: the gallery showed "Nothing up yet" over four stored screenshots.
- `AsNoTracking()` does no identity resolution, so `GroupBy(c => c.Game)` compares
  navigation objects by reference. Group by the id. This made three leaderboards all
  titled the same game.
- A `PageModel` property called `Page` hides `PageModel.Page()`.
- `grid-row: 1 / -1` resolves against the explicit grid only.
- Grid columns do not align across separate grids. A shared column needs a fixed
  width, which is why the portrait column is `--portrait-w`.
- Confirm dialogs built as `confirm('Delete @name?')` are stored XSS with a
  user-supplied name: Razor encodes the quote, the HTML parser decodes it before JS
  sees it. Still outstanding on the character, admin history and spellcraft rows.

## Style

- **No em dashes anywhere.** Not in code comments, not in commit messages, not in
  markdown. No en dash as a connector either, and no parentheses or spaced hyphen
  standing in for one. End the sentence or use a comma.
- Comments say why, not what. The ones worth writing record a decision or a bug
  that is no longer visible in the code.
- Sentence case headings. No decorative emoji. Straight quotes.
- No AI attribution in commit messages. No `Co-Authored-By`, no generated-with
  footer.

## Scope

Do what was asked and nothing adjacent. No extra config flags, no helpful
refactors nobody asked for, no abstractions for a second case that does not exist
yet. Smallest change that solves the problem.
