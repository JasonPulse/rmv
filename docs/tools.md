# Tools

Player utilities, grouped by game.

```
/tools                     categories
/tools/daoc                Dark Age of Camelot
/tools/daoc/roll-parser    chat log -> /random results, highest first
/tools/daoc/spellcraft     item slot + gems -> bonuses, caps, imbue, skill
```

## Roll parser

Ported from `parse.php`, a 2011 script that read a DAoC chat log and grouped
`/random` results by value. The parsing rule is the same; nothing else is.

### What the original did wrong

Worth recording, because it is why the port looks the way it does.

```php
$target = "../daoc/" . basename($_FILES['uploaded']['name']);
move_uploaded_file($_FILES['uploaded']['tmp_name'], $target);
```

The upload was written into a web-served directory under a client-supplied
filename. Uploading `shell.php` gives remote code execution. The one guard
against it was:

```php
if ($uploaded_type == "text/php") { echo "No PHP files<br>"; $ok = 0; }
```

`$uploaded_type` is never assigned anywhere in the file, so that comparison is
always false and the check is dead code. `text/php` is not a real media type
either, and a declared content type is client-controlled regardless.

Then:

```php
$regx = '|] (.*) picks? a random number between 1 and 100: (.*)\r|';
echo $a . " -- " . $rolls[$a] . "<br>";
```

Both captures are `(.*)`, so the player name is arbitrary bytes, and it was
echoed unescaped. A log line naming yourself `<script>...</script>` executed in
the browser of whoever parsed the log.

### What this version does

- **Nothing is written to disk.** The upload is read from the request stream,
  parsed, and dropped. There is no path to construct and no filename to trust.
- **The regex is the validator, not a post-hoc filter.** It is anchored at both
  ends of each line with every quantifier bounded, and the name capture is
  `[A-Za-z]{1,24}`. A hostile name does not get escaped on the way out; it never
  parses in the first place, so it cannot reach the model at all. The tests
  assert this for script tags, event handlers, path traversal, SQL and
  overlong names.
- **The value must be an integer 0-100.** The original used the captured string
  as an array key, so `101` or `87.` became a string key that the output loop
  then never matched, and the roll silently vanished.
- **Bounded work.** 2MB per file, 250k lines, 20k rolls, and a 250ms regex
  match timeout. A 200k-character single line is a test case.
- **Rate limited.** 20 requests per minute per client address, keyed after
  `UseForwardedHeaders` so it partitions on the real caller rather than on
  cloudflared's pod address.
- **Antiforgery token required**, which Razor Pages does by default for POST.

### Parsing rules

Accepted, with or without a timestamp and with or without a trailing period:

```
[Sat Jan 01 12:00:00 2011] Playername picks a random number between 1 and 100: 87
Playername picks a random number between 1 and 100: 87.
You pick a random number between 1 and 100: 42
```

`picks` and `pick` both appear because the game writes "You pick" for your own
roll. Ranges other than 1-100 are ignored, matching the original.

Rolls are grouped by value, highest first. Within a value, names stay in log
order, so a tie reads in the order it happened and a reroll shows as two
entries.

### Things it deliberately does not do

- No other roll ranges. `/random 1000` is a different question.
- Names are alphabetic only, which is what DAoC allows. A name with a digit or a
  space will not parse.
- A file over 2MB is rejected by the request size limit before the handler runs,
  so the response is a plain 400 rather than the friendly message. The limit is
  stated on the form.

## Spellcraft calculator

Its own page, because the game data behind it is still being gathered and the
shape of that ask is most of what there is to say. See `docs/spellcraft.md`.

## Accessibility

Every page is audited with axe-core against the running site, at WCAG 2.1 A and
AA plus best practice, and every page reports zero violations. The audit is a
browser driving the real page, not a static scan of the markup.

Three things it caught that reading the CSS would not have:

`--ink-faint` was `#5f5d57`, which measures 2.90:1 against a character card where
13px text needs 4.5:1. Every use of that token is small text. It is `#828077` now,
same hue and saturation, 4.8:1 or better on every background the site puts it on.
It sits close to `--ink-dim` as a result, and that is the honest outcome: on a
near-black page there is no room for a readable grey that is also fainter than dim.

`.linkish--danger` was `#b8574c` at 3.91:1. Delete and remove are the two
destructive actions on the site, which makes them the worst place for text a reader
has to squint at.

A stray `.tag { opacity: 0.7 }` was dimming every guild tag rather than only the
ones on a game that is no longer active, which both broke contrast and muted a
distinction that was supposed to be visible.

Structural fixes: the history page had no `h2` between its `h1` and the `h3` of the
first game name, so there is a visually hidden "Still going" heading to match the
visible "No longer active" bar. Two admin tables had an empty `th` over their
actions column.

## Layout

`.rule--fine` inherited `.rule`'s `margin-block: clamp(2rem, 5vw, 3.5rem)`. It is
an in-panel separator, so every one of them was adding up to 3.5rem of empty space
above and below, and a panel with three of them was mostly air. It has its own
1rem margin now.

