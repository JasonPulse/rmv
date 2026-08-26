#!/usr/bin/env bash
# Build the site's logo and icon set from the source logo artwork.
#
# The source is a raster logo on a solid near-black field, so the background is
# keyed out by luminance rather than by colour: a colour key would punch holes in
# the logo's own dark navy, and the blue rune glow around the crest is part of
# the design and worth keeping.
#
# Three crops come out of one source image:
#   lockup  the whole thing, crest over the banner. Used as the home page hero.
#   mark    the horned helm alone, in a circle with a gold rim. Used in the
#           masthead and by every icon and favicon: at small sizes the full
#           crest is unreadable, and the helm is the only element that survives.
#
# A crest-only crop was tried for the masthead and abandoned. Any crop height
# slices the gold banner beneath the shield, so it reads as a truncated logo
# rather than a smaller one. CREST_H_FRAC is kept below for reference.
#
# Rerun after replacing the source artwork. Crop rectangles are tied to this
# exact image and are the first thing to re-check if the logo is redrawn.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WWWROOT="$REPO_ROOT/src/Rmv.Web/wwwroot"
OUT="$WWWROOT/img/logo"

# Logo on a near-black field. Used for everything.
SRC_FLAT="${SRC_FLAT:-$HOME/Downloads/Gemini_Generated_Image_vt8qlxvt8qlxvt8q.jpg}"
# Same logo composited on a stone wall. Kept for reference; not built into an
# asset yet.
SRC_STONE="${SRC_STONE:-$HOME/Downloads/Gemini_Generated_Image_cefv21cefv21cefv.jpg}"

# Luminance window mapped to the alpha ramp. The field measures 0-2 and the
# logo's darkest interior sits well above 13, so this keys the field without
# thinning the artwork. Raising the top end makes the logo's darks translucent,
# which shows up as a washed-out logo on a light background.
KEY_LEVELS="${KEY_LEVELS:-0.5%,5%}"

# Where the logo sits in the source canvas is measured, not hardcoded: the
# artwork gets regenerated and lands in a slightly different place each time.
# 4% picks up the blue rune glow, which is part of the design.
BBOX_THRESHOLD="${BBOX_THRESHOLD:-4%}"

# The crops *within* the logo are fractions of its measured size, so a
# regenerated logo at a different scale still works. They assume the same
# composition: crest on top, banner across the bottom, helm at top centre.
# Re-check these against a contact sheet if the layout itself changes.
CREST_H_FRAC=0.671   # where the crest ends and the banner begins. Unused: see above.
MARK_X_FRAC=0.312    # helm crop, left edge
MARK_W_FRAC=0.380    # helm crop, width (square)
MARK16_X_FRAC=0.358  # tighter helm crop for the 16px icon
MARK16_W_FRAC=0.289
MARK16_Y_FRAC=0.022

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

command -v magick >/dev/null || die "imagemagick not found. brew install imagemagick"
[[ -f "$SRC_FLAT" ]] || die "source logo not found at $SRC_FLAT (override with SRC_FLAT=...)"

mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# --- key the background out ------------------------------------------------
BBOX="$(magick "$SRC_FLAT" -colorspace Gray -threshold "$BBOX_THRESHOLD" -format '%@' info:)"
log "logo bbox in source: $BBOX (threshold $BBOX_THRESHOLD)"

log "keying background (levels $KEY_LEVELS)"
magick "$SRC_FLAT" \
  \( +clone -colorspace Gray -level "$KEY_LEVELS" \) \
  -alpha off -compose CopyOpacity -composite \
  -crop "$BBOX" +repage \
  "$WORK/logo.png"

read -r LW LH < <(magick identify -format '%w %h\n' "$WORK/logo.png")
px() { awk -v a="$1" -v b="$2" 'BEGIN{printf "%d", a*b}'; }

MARK_W="$(px "$LW" "$MARK_W_FRAC")"
CROP_MARK="${MARK_W}x${MARK_W}+$(px "$LW" "$MARK_X_FRAC")+0"
MARK16_W="$(px "$LW" "$MARK16_W_FRAC")"
CROP_MARK_16="${MARK16_W}x${MARK16_W}+$(px "$LW" "$MARK16_X_FRAC")+$(px "$LH" "$MARK16_Y_FRAC")"
log "logo ${LW}x${LH}; mark $CROP_MARK; mark16 $CROP_MARK_16"

# --- lockup and crest ------------------------------------------------------
# WebP, not PNG. The artwork is photographic, so PNG lands at 1.5MB for the
# lockup against 240K for WebP at a quality that shows no difference at display
# size. Icons stay PNG because .ico and the web manifest need it.
log "building lockup (webp)"
magick "$WORK/logo.png" -resize 720x \
  -quality 82 -define webp:alpha-quality=92 -strip "$OUT/rmv-lockup.webp"

# --- the mark, and every icon from it --------------------------------------
# A raw crop of the crest reads as a busy rectangle at favicon size. Masking the
# helm into a circle with a gold rim gives it a silhouette that survives 16px.
log "building the mark"
magick "$WORK/logo.png" -crop "$CROP_MARK" +repage -resize 512x512! "$WORK/mark-base.png"
magick -size 512x512 xc:none -fill white -draw 'circle 256,256 256,14' "$WORK/mark-mask.png"
magick "$WORK/mark-base.png" "$WORK/mark-mask.png" \
  -alpha off -compose CopyOpacity -composite \
  -stroke '#c6a97a' -strokewidth 13 -fill none -draw 'circle 256,256 256,20' \
  -strip "$OUT/rmv-mark.png"
# The mark is the icon source, so it does not need to ship at full colour depth.
magick "$OUT/rmv-mark.png" -colors 160 -strip "$OUT/rmv-mark.png"

# Quantized: the mark is a detailed render, and full-colour PNG puts the 512
# icon at 332K for no visible gain at icon sizes.
log "building icons"
magick "$OUT/rmv-mark.png" -resize 180x180 -colors 128 -strip "$OUT/apple-touch-icon.png"
magick "$OUT/rmv-mark.png" -resize 192x192 -colors 128 -strip "$OUT/icon-192.png"
magick "$OUT/rmv-mark.png" -resize 512x512 -colors 128 -strip "$OUT/icon-512.png"

# Multi-resolution .ico. Browsers pick the size they need; 48 shows up on some
# Windows surfaces. The 16px slice is built from the tighter crop, because .ico
# holds an independent image per size and the standard crop is unreadable there.
log "building favicon.ico"
magick "$WORK/logo.png" -crop "$CROP_MARK_16" +repage -resize 128x128! "$WORK/m16.png"
magick -size 128x128 xc:none -fill white -draw 'circle 64,64 64,4' "$WORK/m16-mask.png"
magick "$WORK/m16.png" "$WORK/m16-mask.png" \
  -alpha off -compose CopyOpacity -composite \
  -stroke '#c6a97a' -strokewidth 5 -fill none -draw 'circle 64,64 64,5' \
  -resize 16x16 "$WORK/ico-16.png"

magick "$OUT/rmv-mark.png" -resize 32x32 "$WORK/ico-32.png"
magick "$OUT/rmv-mark.png" -resize 48x48 "$WORK/ico-48.png"
magick "$WORK/ico-16.png" "$WORK/ico-32.png" "$WORK/ico-48.png" \
  -strip "$WWWROOT/favicon.ico"

# --- manifest --------------------------------------------------------------
log "writing site.webmanifest"
cat > "$WWWROOT/site.webmanifest" <<'JSON'
{
  "name": "Results May Vary",
  "short_name": "RMV",
  "icons": [
    { "src": "/img/logo/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/img/logo/icon-512.png", "sizes": "512x512", "type": "image/png" }
  ],
  "theme_color": "#07080c",
  "background_color": "#07080c",
  "display": "standalone",
  "start_url": "/"
}
JSON

if command -v oxipng >/dev/null 2>&1; then
  log "optimising"
  oxipng -o 4 --strip safe -q "$OUT"/*.png
fi

log "done"
printf '  %-34s %-12s %s\n' "FILE" "SIZE" "BYTES"
for f in "$OUT"/*.webp "$OUT"/*.png "$WWWROOT/favicon.ico"; do
  printf '  %-34s %-12s %s\n' \
    "${f#"$WWWROOT/"}" \
    "$(magick identify -format '%wx%h' "$f[0]" 2>/dev/null)" \
    "$(du -h "$f" | cut -f1 | tr -d ' ')"
done
printf '\n  stone-wall source kept for reference, not built: %s\n' "$(basename "$SRC_STONE")"
