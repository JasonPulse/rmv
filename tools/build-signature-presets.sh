#!/usr/bin/env bash
# Signature background presets, from the 2014 generator's backup.
#
# The originals are the guild's own: DAoC screenshots and gradients carrying the
# realm knot, all already 520x160, which is the canvas. They are copied verbatim,
# because re-encoding a gradient introduces banding for no gain.
#
# The thumbnails are derived and committed alongside. Without them the editor's
# picker downloads 1.3MB to show 22 choices; with them it is under 100KB. Run this
# again after adding a preset:
#
#   tools/build-signature-presets.sh ~/Downloads/Daoc/Bgs
set -euo pipefail
cd "$(dirname "$0")/.."

SOURCE="${1:-}"
OUT=src/Rmv.Web/wwwroot/img/sig
THUMBS="$OUT/thumb"

mkdir -p "$THUMBS"

if [ -n "$SOURCE" ]; then
    [ -d "$SOURCE" ] || { echo "no such directory: $SOURCE" >&2; exit 1; }
    echo "copying originals from $SOURCE"
    cp "$SOURCE"/* "$OUT"/
fi

echo "building thumbnails"
count=0
for f in "$OUT"/*.png "$OUT"/*.jpg; do
    [ -f "$f" ] || continue
    name=$(basename "$f")
    # 130x40 is the picker's tile. PNG for both sources, so one <img> shape.
    sips -z 40 130 -s format png "$f" --out "$THUMBS/${name%.*}.png" >/dev/null
    count=$((count + 1))
done

echo "$count presets, $(du -sh "$OUT" | cut -f1) total, $(du -sh "$THUMBS" | cut -f1) of that thumbnails"
