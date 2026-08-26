#!/usr/bin/env bash
# Slice web UI assets out of the Fantasy Nordic GUI kit PSD.
#
# The kit ships one 690MB PSD with 1048 layers and no useful layer names, so the
# mapping from layer index to asset name is recorded in the MANIFEST below. Each
# index was picked by eye from a contact sheet; regenerate that sheet with
# --contact-sheet if you want to pick more.
#
# Layer indices are tied to this exact PSD. If the kit is ever updated, the
# indices move and the manifest has to be rebuilt.

set -euo pipefail

KIT_ZIP="${KIT_ZIP:-$HOME/Downloads/fantasynordicgui.zip}"
PSD_IN_ZIP='01-PSD/UI Fantsy Viking Complete.psd'
FONT_IN_ZIP='02-Font/Vollkorn'

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_IMG="$REPO_ROOT/src/Rmv.Web/wwwroot/img/ui"
OUT_FONT="$REPO_ROOT/src/Rmv.Web/wwwroot/fonts"
WORK="${WORK:-/tmp/rmv-uikit}"
PSD="$WORK/kit.psd"

# index:name:max-width
#
# The PSD art is authored for 4K game UI, so most layers are 2-3x larger than a
# web page needs. max-width is the widest the asset is ever rendered at on the
# site, doubled for retina. Textured assets keep their detail; assets that are
# just stretched geometry get cut down hard.
MANIFEST=(
  # --- In use by rmv.css ---------------------------------------------------
  470:panel-frame-card:651       # trinity-knot corners, on every panel and game card
  582:header-bar-frame:900       # section bars
  576:title-plaque-frame:900     # page titles and game wordmarks
  575:title-plaque-bg:900        # stone fill behind those
  348:divider-hairline:1200      # thin rule inside panels
  337:divider-ornate:1200        # rule between sections
  491:pattern-lattice:400        # hero backdrop, fades downward, repeat-x only
  397:pattern-knotwork:700       # footer band, same

  # The kit's portrait idiom: a stone diamond with a knotted silver frame. Used
  # for the signed-in avatar, which is the thing it was drawn for.
  903:slot-bg:210
  952:slot-frame:214
)

# --- Sliced previously, not currently referenced -----------------------------
#
# Not shipped, because unreferenced files still go into the image and get served.
# The layer indices are the expensive part and are kept here: to bring one back,
# move its line into MANIFEST above and rerun.
#
# Why each one lost its job:
#
#   600:button-bg:600            } the kit's button art has a stepped bracket on
#   599:button-frame:600         } two corners only, which at button size reads as
#   590:button-bg-pill:600       } a lopsided outline. Buttons are CSS now.
#   243:input-frame:700          form controls are CSS: a sliced frame cannot
#                                follow a textarea that the user resizes.
#   246:progress-track:600       } nothing has a progress bar yet.
#   244:progress-fill:600        }
#   581:header-bar-bg:900        stretching a 16:1 texture into a 4:1 bar
#   573:navbar-bg:900            misaligned its baked-in ends; gradients instead.
#   401:panel-frame-tall:700     ornament is one top-centre diamond, which
#                                border-image cannot place.
#   496:panel-frame-inset:693    bracket on the top two corners only, so no slice
#                                both keeps it and avoids stretching the leftover
#                                down the edges. Drawn in CSS: see .panel--inset.
#   381:divider-wide:1400        redundant next to divider-ornate.
#   904:slot-frame-notched:218   an alternative diamond frame, notched at the
#                                bottom rather than knotted at the corner.
#   420:crest-skull:260          the kit's own emblem, replaced by the real logo.

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

require() {
  command -v "$1" >/dev/null 2>&1 || die "$1 not found. brew install ${2:-$1}"
}

extract_psd() {
  [[ -f "$KIT_ZIP" ]] || die "kit zip not found at $KIT_ZIP (override with KIT_ZIP=...)"
  mkdir -p "$WORK"
  if [[ -f "$PSD" ]]; then
    log "PSD already extracted at $PSD"
    return
  fi
  log "extracting PSD from $(basename "$KIT_ZIP") (690MB, takes a few seconds)"
  unzip -o -j "$KIT_ZIP" "$PSD_IN_ZIP" -d "$WORK" >/dev/null
  mv "$WORK/$(basename "$PSD_IN_ZIP")" "$PSD"
}

# Dump every layer's index, size, offset and name. Useful for picking new assets.
enumerate() {
  extract_psd
  log "enumerating layers -> $WORK/layers.txt"
  magick identify -quiet -format '%[scene]|%wx%h|%X%Y|%[label]\n' "$PSD" 2>/dev/null \
    | iconv -f UTF-8 -t UTF-8//IGNORE > "$WORK/layers.txt"
  wc -l < "$WORK/layers.txt" | xargs printf '    %s layers\n'
}

# Render a labelled grid of arbitrary layer indices so they can be judged by eye.
#   ./slice-nordic-ui.sh --contact-sheet 401,470,496,573
contact_sheet() {
  local indices="$1"
  extract_psd
  local dir="$WORK/sheet"
  rm -rf "$dir" && mkdir -p "$dir"
  log "rendering layers $indices"
  magick -quiet "$PSD[$indices]" "$dir/s-%03d.png"
  fonts
  magick montage -background '#141418' -fill '#e8d9b0' \
    -font "$OUT_FONT/Vollkorn-SemiBold.ttf" -pointsize 30 \
    -label '%f  %wx%h' "$dir"/s-*.png \
    -tile 6x -geometry 320x250+10+10 "$WORK/contact-sheet.png"
  log "wrote $WORK/contact-sheet.png"
  log "map grid position back to index by the order you passed: $indices"
}

fonts() {
  mkdir -p "$OUT_FONT"
  if [[ -f "$OUT_FONT/Vollkorn-Regular.ttf" ]]; then return; fi
  log "extracting Vollkorn -> $OUT_FONT"
  unzip -o -j "$KIT_ZIP" "$FONT_IN_ZIP/static/*.ttf" "$FONT_IN_ZIP/OFL.txt" -d "$OUT_FONT" >/dev/null
  # The site only loads four weights. Drop the rest so they are not served.
  find "$OUT_FONT" -name '*.ttf' \
    ! -name 'Vollkorn-Regular.ttf' \
    ! -name 'Vollkorn-Medium.ttf' \
    ! -name 'Vollkorn-SemiBold.ttf' \
    ! -name 'Vollkorn-Bold.ttf' \
    -delete
  if command -v woff2_compress >/dev/null 2>&1; then
    log "converting to woff2"
    for f in "$OUT_FONT"/*.ttf; do woff2_compress "$f" >/dev/null && rm "$f"; done
  else
    log "woff2_compress not found, shipping ttf (brew install woff2 to shrink ~60%)"
  fi
}

slice() {
  extract_psd
  mkdir -p "$OUT_IMG"

  # One magick invocation for all layers: reading the PSD is the expensive part.
  local indices names raw
  indices="$(printf '%s\n' "${MANIFEST[@]}" | cut -d: -f1 | paste -sd, -)"
  raw="$WORK/raw"
  rm -rf "$raw" && mkdir -p "$raw"
  log "slicing ${#MANIFEST[@]} layers in one pass"
  magick -quiet "$PSD[$indices]" "$raw/r-%03d.png"

  # magick preserves the order of the scene list, so raw/r-NNN maps to
  # MANIFEST[NNN]. Rename, trim the transparent margin, and strip metadata.
  local i=0
  for entry in "${MANIFEST[@]}"; do
    local name maxw src
    IFS=: read -r _ name maxw <<< "$entry"
    src="$(printf '%s/r-%03d.png' "$raw" "$i")"
    [[ -f "$src" ]] || die "expected $src from layer ${entry%%:*}"
    # Only shrink, never upscale: '>' means resize just if wider than maxw.
    magick "$src" -trim +repage -resize "${maxw}x>" -strip "$OUT_IMG/$name.png"
    printf '    %-26s %-12s %s\n' "$name.png" \
      "$(magick identify -format '%wx%h' "$OUT_IMG/$name.png")" \
      "$(du -h "$OUT_IMG/$name.png" | cut -f1 | tr -d ' ')"
    i=$((i + 1))
  done

  if command -v oxipng >/dev/null 2>&1; then
    log "optimising with oxipng"
    oxipng -o 4 --strip safe -q "$OUT_IMG"/*.png
  elif command -v pngcrush >/dev/null 2>&1; then
    log "optimising with pngcrush"
    for f in "$OUT_IMG"/*.png; do pngcrush -q -ow "$f" >/dev/null 2>&1 || true; done
  else
    log "no png optimiser found (brew install oxipng to shrink ~30%)"
  fi

  log "total: $(du -sh "$OUT_IMG" | cut -f1) across $(ls "$OUT_IMG" | wc -l | tr -d ' ') files"
}

main() {
  require magick imagemagick
  require unzip
  case "${1:-slice}" in
    slice)          fonts; slice ;;
    fonts)          fonts ;;
    --enumerate)    enumerate ;;
    --contact-sheet) [[ -n "${2:-}" ]] || die "usage: $0 --contact-sheet 401,470,496"; contact_sheet "$2" ;;
    -h|--help)
      sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      printf '\nusage:\n  %s                      slice all assets + fonts\n' "$(basename "$0")"
      printf '  %s fonts                extract fonts only\n' "$(basename "$0")"
      printf '  %s --enumerate          dump all 1048 layers to %s/layers.txt\n' "$(basename "$0")" "$WORK"
      printf '  %s --contact-sheet 1,2  render those layers as a labelled grid\n' "$(basename "$0")"
      ;;
    *) die "unknown command: $1 (try --help)" ;;
  esac
}

main "$@"
