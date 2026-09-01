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

Rendering, per day: one render per signature whose data changed, during the pass
that already runs. Twenty members is twenty renders of a 520x160 canvas, which is
single-digit milliseconds each. The old design did more work than that per page
view.

Storage: about 100KB per rendered signature and at most two backgrounds per member
at the canvas size. Twenty members is a few megabytes, against a gallery that
already holds screenshots.

## Decisions needed

1. **Image library.** ImageSharp or SkiaSharp.

   ImageSharp is pure managed, which matters because the Dockerfile's runtime stage
   deliberately has no `RUN` steps and SkiaSharp's usual Linux package wants
   fontconfig. SkiaSharp does publish a `NoDependencies` native package that avoids
   that. On licences, ImageSharp 2.x is Apache 2.0 and 3.x is the Six Labors Split
   License, which this repo qualifies for free under its open source clause since
   it is public. SkiaSharp is MIT over BSD Skia with no conditions to read.

   Recommendation: ImageSharp, pinned, for the pure-managed build. It also covers
   the gallery thumbnails that are still outstanding.

2. **Fonts.** A curated set of OFL faces, or specific ones from the old 116. The v1
   default was Centaur, which is not OFL.

3. **Canvas sizes.** One size, 520x160, or a small choice such as 400x120 and
   520x160.

4. **Backgrounds.** Keep the 22 v1 presets, which are DAoC screenshots of unknown
   provenance, or start with a set drawn from the site's own kit and the gallery.

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
