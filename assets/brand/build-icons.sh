#!/usr/bin/env bash
# Reproducible icon build for Squirrel.
#
# Renders the primary amber tile master (squirrel-tile.svg) to PNGs across the
# size ramp, then packs platform icon containers:
#   - squirrel.ico   (Windows / Avalonia WindowIcon): 16/24/32/48/256
#   - squirrel.icns  (macOS): 16..1024 + Retina slots
#   - favicon PNGs   (16/32/48) + apple-touch-icon (180)
#
# Requires: ImageMagick (magick), icotool (icoutils), python3.
set -euo pipefail
cd "$(dirname "$0")"

SRC="squirrel-tile.svg"
OUT="icons"
PNG="$OUT/png"
mkdir -p "$PNG"

render() { # size
  magick -background none "$SRC" -resize "${1}x${1}" "$PNG/tile-${1}.png"
}

echo "Rendering PNG ramp from $SRC ..."
for s in 16 24 32 48 64 96 128 180 256 512 1024; do
  render "$s"
done

echo "Packing $OUT/squirrel.ico ..."
icotool -c -o "$OUT/squirrel.ico" \
  "$PNG/tile-16.png" "$PNG/tile-24.png" "$PNG/tile-32.png" \
  "$PNG/tile-48.png" "$PNG/tile-256.png"

echo "Packing $OUT/squirrel.icns ..."
python3 pack-icns.py "$OUT/squirrel.icns" \
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
