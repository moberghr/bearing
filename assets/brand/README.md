# Squirrel — brand assets

Vector identity for **Squirrel** (SQL query editor), mark direction **1a**: a sitting
squirrel with a bushy curled tail holding an acorn. Source of truth is the SVG path data
(100×100 viewBox) reproduced from the design handoff.

## Source SVGs
| File | Use |
|------|-----|
| `squirrel-amber.svg` | Primary two-tone amber mark (body `#FF9E3B`, tail `#E6883B`, acorn `#DCD7BA`, eye `#16161D`). |
| `squirrel-ink.svg`   | Mono ink mark for light backgrounds (eye punched in cream). |
| `squirrel-light.svg` | Mono cream mark for dark backgrounds. |
| `squirrel-tile.svg`  | 256×256 app-icon tile: amber mark on the dark rounded-superellipse tile (r56, 160° `#20202B→#16161D` gradient, `#2A2A37` border). Master for all raster icons. |
| `squirrel-wordmark.svg` | Horizontal lockup (mark + "Squirrel / SQL EDITOR"). Text is Space Grotesk 700/600 — install or embed the font when rasterizing. |

## Generated icons (`icons/`)
Rebuild with `./build-icons.sh` (needs ImageMagick, `icotool` from icoutils, python3):

- `squirrel.ico` — Windows / Avalonia window icon (16/24/32/48/256).
- `squirrel.icns` — macOS (16–1024 + Retina slots), packed by `pack-icns.py`.
- `favicon-16/32/48.png`, `apple-touch-icon.png` (180) — web.
- `png/` — the intermediate size ramp.

## In-app usage
- **Window icon**: `squirrel.ico` is copied to `src/Squirrel.App/Assets/` (an `AvaloniaResource`)
  and referenced by `MainWindow.axaml` via `Icon="/Assets/squirrel.ico"`.
- **Brand mark control**: `src/Squirrel.App/Themes/Brand.axaml` exposes the mark as
  `SquirrelMark.Amber` / `.Ink` / `.Light` `DrawingImage` resources
  (`<Image Source="{StaticResource SquirrelMark.Amber}"/>`), shown at the top of the left rail.

Regenerating `squirrel.ico`? Re-copy it into `src/Squirrel.App/Assets/` afterwards.
