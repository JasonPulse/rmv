# Forum signature generator

A plan, from reading the Uthgard v1 backup in `Daoc.zip`. Nothing is built yet.

## What v1 was

16 PHP files, GD for rendering, jQuery 1.7 and SWFUpload for the editor, MySQL for
designs, 116 TTF fonts, 22 preset backgrounds and 10 member uploads.

The important part is how it served an image, because it is the thing we must not
copy. `sig.php?chars=property&design=12`:

1. curled the Uthgard herald for each named character
2. scraped eight fields out of the HTML with regexes
3. rendered a PNG with GD
4. sent `Cache-Control: no-cache, must-revalidate` and `Expires: 1997`

Every forum page view anybody loaded did all four. That is one outbound herald
fetch and one image render per view, with caching explicitly disabled. It is also
why requests for `sig.php` are still arriving ten years after it was switched off:
the URL is embedded in forum posts that still exist.

### The layout model

A grid of twelve text slots, three columns by four rows, each a template string.
One font, one size, one colour for the whole image. Column x and row y were either
`auto` or a number typed into a box. There was no drag and drop; "placement" meant
typing `leftStart=130`.

Default template, which is a good target for the new one to reproduce exactly:

```
%C%SPLevel %L %RA %P%SP%G%SP%SL%SP%RP Lwrp %W
```

rendering as "Property - Level 50 Norseman Skald - Grand Poobah - 8L0 - 1,234,567
Lwrp 12,345".

### The tokens

Single character, meaning the first one named:

```
%C name   %L level   %RA race   %P class   %G guild
%SL realm rank   %RP realm points   %W last week RPs   %K kills
```

Every character, expanded one line per character:

```
%AC %AL %CR %AP %AG %AS %AR %AW %AK
```

Totals across the named characters:

```
%TL total levels   %TRL total realm rank levels
%TRP total realm points   %TW total last week   %TK total kills
```

Two specials: `%SP` is a " - " separator and `%JT` is a justify break that splits a
line into two columns aligned on the longest left half.

### Canvas sizes

The default black background auto-sized to the text, minimum 520 wide. Preset
backgrounds were 520x160. Member uploads ran 520x98 to 576x190. So 520x160 is the
shape a forum signature is, and the presets are all one size.

## What v2 added, which is less than it sounds

He had a second backup, `daoc_v2.zip`, dated 2014. Compared file by file against
v1, ignoring line endings, fifteen of the seventeen PHP files are identical and
the two that differ do so for these reasons:

1. **sig.php learned to recognise crawlers.** If the user agent contains "bot",
   "spider" or "crawler" it stops short of creating a new design row. So the
   traffic worry is not hypothetical: v2 was already being crawled hard enough to
   fill its table with junk designs, and sniffing user agents was the defence.
2. **customize.php gained Google Analytics.**
3. `parse.php` is new, and is the roll parser, which the site already has at
   /tools/daoc/roll-parser.
4. Thirty-two more member backgrounds in UserBG. The 22 presets are unchanged.

The renderer, the twelve-slot schema, the start-point columns and every token are
byte for byte the same. The token set is identical in both: no additions, no
removals.

**Neither version has drag and drop.** v2's editor is the same twelve text inputs
and the same seven numeric start points; what it added is a preview that updates on
every keystroke and floats alongside as the page scrolls, which is probably the
interactivity being remembered. The schema could not express free positions
anyway: three x values, one per column, and four y values, one per row.

So drag and drop stays new work, and the plan below is unchanged by v2.

### The templates people actually used

From v2's help page, worth offering as starting points in the editor rather than
leaving somebody with an empty canvas:

```
%C Level: %L %P %SL %RP
%AC Level: %AL %CR %AP %AS
%AC %AS %AP %TRP Realm Points %TRL Realm Levels Earned %TK Total Kills
```

## Terra, which is the one with drag and drop

`Terra.zip`, the Tera generator, is the third and the one he remembered. It is a
different program: same 116-font folder and the same herald scraping, but the
layout model was rewritten.

### The model

Three columns, each holding a list rather than a fixed slot:

```
textFields  = "1~%C;2~Some text;3~%L;"
colors      = "1~255,255,255;2~200,180,90;"
startingPos = "1~12,52;2~140,90;"
```

One entry per text field, keyed by an index, so a signature has as many fields as
somebody adds and each carries its own text, its own colour and its own x and y.
Font and size stayed global to the whole image.

That is the free positioning the twelve-slot versions could not express, and it is
what my element list already matches. Per-element font, size, alignment, outline
and character binding are additions on top.

### How the drag actually worked, which is worth stealing

A popup whose background is the chosen signature background at the preview's exact
size, holding one `<img>` per text field. Each of those images is
`placeDivImg.php?f=font&fs=size&t=text&c=colour`: the server rasterises that one
field as a transparent PNG in the real font, and jQuery UI's `draggable()` moves
the picture around. On close, `position()` is read off each one and serialised into
startingPos.

So you drag the actual rendered glyphs over the actual background, and the editor
cannot disagree with the renderer about text metrics. In 2014, with GD's metrics
unavailable to a browser and no webfonts, that was the only way to be faithful.

The modern equivalent is cheaper and does not put a render on every keystroke: the
site already serves Vollkorn as woff2, so the browser can lay out the same face at
the same size, and a real render on drop catches any drift. Same intent, no server
work while dragging. There are two hand-tuned constants in the old pair, a minus 30
on the y in the renderer against a plus 25 inside placeDivImg, which is what
compensating for a baseline by trial and error looks like; drawing from the top of
the text removes the need for either.

### The thing neither backup has

Terra's default design is `1~%C;`, so tokens were meant to work. They do not. In
its createCustomImage.php the line that does the substitution,

```php
$newText = str_replace($keys,$values,$text);
```

is inside a commented-out block, and the render loop writes the raw field text
instead. The keys and values are still computed on every request and then thrown
away. A Terra signature drew the literal characters `%C`.

So the version with the tokens has no drag and drop, and the version with the drag
and drop has no tokens. The new one is the first to have both, which is worth
knowing before comparing it to a memory of either.

### Two smaller things

The canvas in all three versions was the background image's own size, so a
signature was 520x160 for a preset and whatever a member uploaded otherwise. Fixed
at 520x160 here, because an editor whose canvas changes shape under the design is
harder to use than one that does not, and uploads are re-encoded to it anyway.

Terra's update.php writes `gif='0'` into a column nothing else touches, so animated
signatures were started and abandoned. Not planned here either.

## What carries over

Feature parity means all of the above: the twelve slots' worth of expressive
power, every token, both specials, the preset backgrounds, member uploads, a font
choice, a colour, and a stable URL that a forum post can embed forever.

## What changes, and why

1. **Rendered on write, not on read.** The signature PNG is built when its design
   changes or when its characters' stats change, stored as bytes, and served like a
   portrait: an ETag, a 304 on revalidation, no rendering on the request path. The
   daily herald pass already knows when a character moved.
2. **Owned by a member.** v1 kept the design id in a cookie and looked it up with
   no ownership check, so any design was editable by anyone who guessed a number.
   Editing sits behind the approved-member policy. The served image stays public,
   because a forum has no cookies, and is addressed by an opaque slug rather than
   anything that identifies an account.
3. **Uploads are bounded.** v1 allowed jpg, gif and png up to two gigabytes. Two
   backgrounds per member, re-encoded to the canvas on the way in, so what is
   stored is bounded by the canvas and not by what somebody had on their desktop.
   The gallery's ImageProbe and CappedRead already do the sniffing and the capping.
4. **Fonts are curated.** 116 fonts of unknown provenance is a licensing problem
   for a public site doing server-side rasterisation. The site already ships
   Vollkorn under the SIL OFL, and a handful of OFL faces covers everything a
   signature needs.
5. **Drag and drop.** New, not parity. Elements are positioned rather than typed
   into a grid, which also removes the twelve fixed slots.

## Data model

```
SignatureDesign      one per member, maybe two later
  MemberId, Slug (opaque, in the public URL)
  CanvasWidth, CanvasHeight
  BackgroundKind: Colour | Preset | Upload
  BackgroundKey (preset name), BackgroundId (upload), BackgroundColour
  Elements: JSON, validated on write
  UpdatedAt

SignatureBackground  the member's own, at most two
  MemberId, Bytes, ContentType, Width, Height, UploadedAt

SignatureRender      the cache, one row per design
  DesignId, Bytes, ContentType, Version (digest of the bytes), RenderedAt
  SourceVersion (digest of design + the character data it drew)
```

`SignatureRender.SourceVersion` is what makes re-rendering cheap and correct: the
daily pass digests the design plus the values it would draw, and re-renders only
when that digest moves. Same idea as a portrait's version being the picture, for
the same reason.

An element:

```json
{ "type": "text", "x": 12, "y": 24, "align": "left",
  "font": "vollkorn-bold", "size": 18, "colour": "#e8d8a0",
  "outline": "#000000", "characterId": 4,
  "template": "%Name% - Level %Level% %Class%" }
```

Every field is clamped on write: at most twelve elements, template at most 120
characters, x and y inside the canvas, font from an allowlist, size 8 to 48,
colours parsed rather than trusted.

## Tokens, new set

Readable names rather than v1's two-letter codes, since there is no back
compatibility to keep: the old designs live in a MySQL table we do not have.
`%%` is a literal percent.

Bound to the element's character:

```
%Name% %Level% %Class% %Race% %Realm% %Guild%
%Rank% %Score% %Kills% %Deaths% %Game% %Seen%
```

Across every character the member has, on every herald, which is the new part:

```
%User%        their handle here
%AllChars%    how many characters they have added
%AllGames%    how many distinct games those span
%AllLevels%   levels summed
%AllScore%    score summed, which is realm points plus job levels plus achievement points
%AllKills%    kills summed
%Since%       the year of their oldest character
```

So his example works as written: `%User% has played %AllChars% characters in
%AllGames% games`.

Multiple characters in one signature is handled by binding, not by indexed tokens:
each text element names the character it draws, so a second line about a second
character is a second element. That is what replaces v1's `%AC` family looping
every character onto its own line.

## The traffic and CPU budget

This is the constraint that shapes the design, so here are the numbers.

Serving, per image request: one indexed row read of about 100KB and no rendering.
Ten forum signatures loading at once is ten of those, which is roughly what one
page of the gallery already costs.

Cloudflare is in front, so `Cache-Control: public, max-age=900` plus an ETag means
repeat views are answered at the edge and never reach the homelab at all. The
tradeoff is a signature up to fifteen minutes stale, which for a stat that updates
daily is nothing. A per-IP rate limit on the route is the backstop for anything
pathological.

Crawlers need no special handling here, which is worth saying because v2 sniffed
user agents for "bot", "spider" and "crawler" to protect itself. It had to: a
crawler hitting the generator created a database row and every hit on an image
rendered one. In this design a crawler gets the same cached bytes as anybody else
and creates nothing, so there is no reason to guess at what it is.

Rendering, per day: one render per signature whose data changed, during the pass
that already runs. Twenty members is twenty renders of a 520x160 canvas, which is
single-digit milliseconds each. The old design did more work than that per page
view.

Storage: about 100KB per rendered signature and at most two backgrounds per member
at the canvas size. Twenty members is a few megabytes, against a gallery that
already holds screenshots.

## Decisions taken

Asked and answered on 2026-08-30.

1. **ImageSharp**, for the pure-managed build. The Dockerfile's runtime stage has
   no `RUN` steps on purpose, to keep QEMU out of a cross-platform build, and
   SkiaSharp's usual Linux native package wants fontconfig. ImageSharp needs
   nothing from the base image. Licensing: 2.x is Apache 2.0 and 3.x is the Six
   Labors Split License, which this repo qualifies for free under its open source
   clause, the repository being public.

2. **A curated set of SIL OFL faces**, including the Vollkorn the site already
   ships, rather than the 116 from the backup. Most of those are dafont-era
   freeware whose terms tend to exclude exactly this use: server-side
   rasterisation on a public site. The TTF cut of Vollkorn goes in beside the
   woff2 already in wwwroot/fonts, under the same OFL.txt.

3. **The 22 v1 preset backgrounds**, which are already 520x160 and make the new
   one look like the old one. A set built from the guild's own material can follow
   later as its own piece of work.

4. Canvas is **520x160**, which is what every v1 preset was and what a forum
   signature is. A second smaller size can follow once anyone asks.

## Built

Steps one to five, on 2026-09-01. What is running:

- `SignatureTokens` and `SignatureData`: the token set, resolved against a character
  and the member's totals across every herald.
- `SignatureRenderer` over ImageSharp 2.1.13 and Drawing 1.0.0, the Apache 2.0 pair.
  Five OFL faces: Vollkorn, Cinzel, UnifrakturMaguntia, Oswald and IM Fell English,
  each with its own licence beside it.
- `SignatureDesignReader`: the boundary. Everything a browser sends is parsed and
  clamped, including the character binding against the ones the member owns.
- `SignatureService`: one signature per member, an opaque slug, and the decision
  about when to re-render. `SourceVersion` digests the design plus every string the
  elements resolve to, so a pass over a member who did nothing writes nothing.
- `SignatureEndpoint` at `/sig/{slug}.png`: stored bytes, an ETag, 304 on
  revalidation, `max-age=900` with `stale-while-revalidate`, and a per-address rate
  limit.
- The editor at `/tools/signature`: drag with a pointer, nudge with arrows, a token
  palette, per-line font, size, colour, outline, alignment and character, the 22
  presets, and up to two uploads re-encoded to the canvas.
- The herald pass redraws the signatures of members whose characters moved, once per
  member rather than once per character.

### Measured, not assumed

Ten forum signatures loading at once against the local stack: **43ms total wall
clock, no renders**. One cold request is 22ms and 24KB; a revalidation is 4.5ms and
no bytes. A render is about 15ms and happens once per member per day.

The editor's canvas against the renderer, at the three default sizes: text widths
of 486, 310 and 414 pixels in the browser against 483, 307 and 412 from ImageSharp.
Within three pixels over 490, so what is dragged is where it draws.

### Left out on purpose

- One weight per face. SixLabors.Fonts 1.0 does not read a variable font's weight
  axis, so bold would mean shipping a second file per family. Nobody has asked.
- No animated GIF. Terra started one and abandoned it.
- The canvas is fixed at 520x160 rather than following the background.

## Build order

Each step ships on its own and is useful before the next exists.

1. Tokens and the render, headless. `SignatureText` resolving templates against a
   character and a member, and `SignatureRenderer` producing PNG bytes from a
   design. Both pure, both tested against a fixture, no page and no route.
2. The public route. One design per member, default template, preset backgrounds
   only, rendered on write and cached, with the caching headers and the rate limit.
   At this point a forum can embed it.
3. The editor. Drag and drop, live CSS preview while dragging, the server's own
   render on save, tokens inserted from a palette rather than typed.
4. Member backgrounds, re-encoded to the canvas, two each.
5. The aggregate tokens across heralds, and whatever fun ones suggest themselves
   once the rest is real.
