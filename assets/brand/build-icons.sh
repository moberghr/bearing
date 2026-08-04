#!/usr/bin/env bash
# Reproducible icon build for Bearing.
#
# Renders the primary graphite tile master (bearing-tile.svg) to PNGs across the
# size ramp, then packs platform icon containers. Sizes <=24px come from
# bearing-tile-small.svg instead — the full mark's bore detail and pitch circle
# turn to mush that small, which is what BRAND.md's markSolid variant exists for.
#   - bearing.ico   (Windows / Avalonia WindowIcon): 16/24/32/48/256
#   - bearing.icns  (macOS): 16..1024 + Retina slots
#   - favicon PNGs   (16/32/48) + apple-touch-icon (180)
#
# Requires: ImageMagick (magick), icotool (icoutils), python3.
set -euo pipefail
cd "$(dirname "$0")"

SRC="bearing-tile.svg"
SRC_SMALL="bearing-tile-small.svg"
SMALL_MAX=24            # sizes at or below this render from the simplified master
OUT="icons"
PNG="$OUT/png"
mkdir -p "$PNG"

render() { # size
  local src="$SRC"
  (( $1 <= SMALL_MAX )) && src="$SRC_SMALL"
  magick -background none "$src" -resize "${1}x${1}" "$PNG/tile-${1}.png"
}

echo "Rendering PNG ramp from $SRC (<=${SMALL_MAX}px from $SRC_SMALL) ..."
for s in 16 24 32 48 64 96 128 180 256 512 1024; do
  render "$s"
done

echo "Packing $OUT/bearing.ico ..."
icotool -c -o "$OUT/bearing.ico" \
  "$PNG/tile-16.png" "$PNG/tile-24.png" "$PNG/tile-32.png" \
  "$PNG/tile-48.png" "$PNG/tile-256.png"

echo "Packing $OUT/bearing.icns ..."
python3 pack-icns.py "$OUT/bearing.icns" \
  16:"$PNG/tile-16.png"   32:"$PNG/tile-32.png"   64:"$PNG/tile-64.png" \
  128:"$PNG/tile-128.png" 256:"$PNG/tile-256.png" 512:"$PNG/tile-512.png" \
  1024:"$PNG/tile-1024.png"

echo "Favicons ..."
cp "$PNG/tile-16.png"  "$OUT/favicon-16.png"
cp "$PNG/tile-32.png"  "$OUT/favicon-32.png"
cp "$PNG/tile-48.png"  "$OUT/favicon-48.png"
cp "$PNG/tile-180.png" "$OUT/apple-touch-icon.png"

echo "Done. Artifacts in $OUT/"
ls -la "$OUT"
