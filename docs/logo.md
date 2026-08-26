# The logo and icons

`tools/build-logo-assets.sh` turns the source logo artwork into everything the
site serves. Rerun it after replacing the artwork.

```bash
./tools/build-logo-assets.sh
SRC_FLAT=~/path/to/new-logo.jpg ./tools/build-logo-assets.sh
```

The source is not in the repo. It lives in `~/Downloads` and the script takes its
path from `SRC_FLAT`.

## Geometry is measured, not hardcoded

Where the logo sits in the source canvas is found by thresholding at 4%, which
picks up the blue rune glow. The crops *inside* the logo are fractions of its
measured size, so a regenerated logo at a different scale still works without
re-measurement. They assume the same composition: crest on top, banner across
the bottom, helm at top centre. If the layout itself changes, re-check them
against a contact sheet.

The fractions were calibrated by eye and then confirmed against hand
measurements on a regenerated logo: derived crest 456 against 455 measured,
derived helm 253+208 against 250+205.

## Keying the background out

The source is a raster logo on a near-black field. The field measures 0 to 2 and
has no vignette, and the logo's own darkest interior sits well above 13, so the
background comes off by mapping luminance to alpha over a narrow window:

```
-level 0.5%,5%
```

A colour key would be wrong here: the logo's deep navy is close enough to the
field that keying by colour punches holes in the shield. The luminance window
also preserves the blue rune glow around the crest, which is part of the design.

Push the top of that window up and the logo's dark areas go translucent. That is
invisible on this site's near-black background and obvious on a white one, which
is how it was caught.

## Three crops, because one does not work everywhere

| Asset | What it is | Where |
|---|---|---|
| `rmv-lockup.webp` | crest over the banner | home hero |
| `rmv-crest.webp` | crest, banner cropped | masthead |
| `rmv-mark.png` | helm in a gold ring | every icon |

The banner text is illegible below roughly 300px wide, so the masthead gets the
crest without it. At 32px the whole crest is unreadable mush; the horned helm is
the only element that survives, which is why every icon derives from it rather
than from the full logo.

The helm is masked into a circle with a gold rim taken from the logo
(`#c6a97a`). A raw crop reads as a busy rectangle with clipped horns; the ring
gives it a silhouette that holds at small sizes.

## Sizes

`favicon.ico` carries 16, 32 and 48. The 16px slice is built from a *tighter*
helm crop than the others: at that size the standard crop loses the horns
completely. `.ico` holds an independent image per size, so this costs nothing.

16px is still the weakest size. A crest this detailed does not reduce well, and
most current browsers ask for 32 on a high-DPI display anyway.

## Formats

The lockup and crest ship as WebP. The artwork is photographic, so PNG put the
lockup at 1.5MB against 352K for WebP with no visible difference at display
size. Icons stay PNG because `.ico` and the web manifest need it, and they are
quantized to 128 colours: full depth cost 332K for the 512 icon and gained
nothing at icon dimensions.

## Palette

The site's accents are sampled from the logo, so the two cannot drift:

| Token | Value | Source in the logo |
|---|---|---|
| `--accent` | `#5aa5cc` | rune glow |
| `--accent-mid` | `#2d6294` | shield blue |
| `--accent-deep` | `#132b51` | deep navy |
| `--accent-warm` | `#c6a97a` | antique gold |
| `--gold-bright` | `#f0edd9` | highlight |

## Not used yet

The second source image is the same logo composited on a stone wall. The script
records its path and builds nothing from it. It would work as a hero backdrop,
but the logo is already the hero, so it would be the logo twice.

## Still slightly redundant

The duplicate "DARK AGE OF CAMELOT" ribbon is gone from the artwork. What remains
is "GUILD SINCE 2001" directly above "EST. DARK AGE OF CAMELOT", which say
roughly the same thing twice. The masthead tagline also reads "SINCE DARK AGE OF
CAMELOT", though the masthead crop excludes the logo's own text so they never
appear together.
