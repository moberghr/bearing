# Bearing — brand assets

Vector identity for **Bearing** (SQL query editor): a **ball bearing** — precision, low
friction, engineered tolerance. Machined teal (`#35D0BE`) on cool graphite.

The mark is **generated geometry**, not a drawn path. On a 100×100 viewBox centred at (50,50):

| Element | Construction |
|---|---|
| Outer race | `circle r=44`, stroke, width **7** |
| Pitch circle | `circle r=31`, stroke, width **1.6**, opacity **.4** |
| 8 balls | `r=6.6`, filled, centres at `(50 + 31·cos a, 50 + 31·sin a)` for `a = i/8·2π − π/2` (first at 12 o'clock) |
| Inner race (bore) | `circle r=15`, stroke, width **7** |

Do not redraw by eye — regenerate from the construction above. See
`docs/design/bearing/BRAND.md` §The mark for the full spec.

## Source SVGs
| File | Use |
|------|-----|
| `bearing-duo.svg`   | Primary duo-tone mark (races teal `#35D0BE`, balls steel `#D8DEE6`). Default, on dark. |
| `bearing-ink.svg`   | Mono ink mark (`#0F1319`) — on teal and on light. |
| `bearing-steel.svg` | Mono steel mark (`#D8DEE6`) — on dark. |
| `bearing-solid.svg` | The **≤16px** simplification: bore detail and pitch circle collapse (outer `r=44` width 12, balls `r=7`, inner `r=12` filled). Favicon, tray, tiny list rows. |
| `bearing-tile.svg`  | 256×256 app-icon tile: duo mark (152px, ≈59%) on the graphite rounded tile (r56, 155° `#1E2630→#12161C` gradient, `#2A323C` border). Master for raster icons ≥32px. |
| `bearing-tile-small.svg` | Same tile with the **solid** mark. Master for raster icons ≤24px. |
| `bearing-wordmark.svg` | Horizontal lockup (mark + "Bearing / SQL EDITOR"). Text is Space Grotesk 700/600 — install or embed the font when rasterizing. |

## Generated icons (`icons/`)
Rebuild with `./build-icons.sh` (needs ImageMagick, `icotool` from icoutils, python3):

- `bearing.ico` — Windows / Avalonia window icon (16/24/32/48/256).
- `bearing.icns` — macOS (16–1024 + Retina slots), packed by `pack-icns.py`.
- `favicon-16/32/48.png`, `apple-touch-icon.png` (180) — web.
- `png/` — the intermediate size ramp.

The script renders sizes ≤24px from `bearing-tile-small.svg` and everything above from
`bearing-tile.svg`, so the small icons get the solid mark automatically.

## In-app usage
- **Window icon**: `bearing.ico` is copied to `src/Bearing.App/Assets/` (an `AvaloniaResource`)
  and referenced by `MainWindow.axaml` via `Icon="/Assets/bearing.ico"`.
- **Brand mark control**: `src/Bearing.App/Themes/Brand.axaml` exposes the mark as
  `BearingMark.Duo` / `.Ink` / `.Steel` / `.Solid` `DrawingImage` resources
  (`<Image Source="{StaticResource BearingMark.Duo}"/>`), shown at the top of the left rail.

Regenerating `bearing.ico`? Re-copy it into `src/Bearing.App/Assets/` afterwards.
