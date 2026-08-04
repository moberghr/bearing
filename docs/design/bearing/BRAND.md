# Handoff: Bearing — Brand

Companion to `README.md`. Reference: `Bearing - Logo.dc.html` (a design prototype, not production code).

## Concept
A **ball bearing** — precision, low friction, engineered tolerance. Machined teal on cool graphite. Replaces the Squirrel mark and name.

## The mark
Generated geometry on a **100 × 100 viewBox**, centered at `(50, 50)`. Reproduce exactly (Avalonia: `PathIcon` / `DrawingImage`, or ship an SVG asset) — do not redraw by eye.

**Primary mark** (`markDuo`, ring teal `#35D0BE`, balls steel `#D8DEE6`):
1. Outer race — `circle r=44`, no fill, stroke `ringColor`, stroke-width **7**.
2. Pitch circle — `circle r=31`, stroke `ringColor`, width **1.6**, opacity **.4**.
3. **8 balls** — `r=6.6`, filled `ballColor`, centers at `(50 + 31·cos a, 50 + 31·sin a)` for `a = i/8 · 2π − π/2`, `i = 0…7` (first ball at 12 o'clock).
4. Inner race (bore) — `circle r=15`, stroke `ringColor`, width **7**.

**Variants** (same construction, colors swapped):
| Variant | Ring | Balls | Use |
|---|---|---|---|
| `markDuo` | `#35D0BE` | `#D8DEE6` | Default, on dark |
| `markInk` | `#0F1319` | `#0F1319` | On teal and on light |
| `markSteel` | `#D8DEE6` | `#D8DEE6` | Monochrome on dark |
| `markSolid` | single color, see below | | **≤16px only** |

**`markSolid`** (small-size simplification — the bore detail and pitch circle collapse): outer `r=44` stroke width **12**; 8 balls `r=7` on the same `r=31` pitch circle; inner `r=12` **filled**. Use at 16px and below (favicon, tray, tiny list rows).

## App icon
256 × 256 tile, radius **56px** (≈22%), mark at **152px** (≈59% of the tile).
- **On graphite** (default): `linear-gradient(155deg, #1E2630, #12161C)`, 1px `#2A323C` border, shadow `0 30px 60px -20px rgba(0,0,0,.75)`, `markDuo`.
- **Flat teal**: `#35D0BE` fill, `markInk`.
- **On light**: `#EAEEF3` fill, `markInk`.

Scale steps shown on the sheet: 96px tile / 60px mark (radius 22) · 56 / 36 (13) · 32 / 21 (8) · 16 / 12 (4, `markSolid`).

## Lockups
- **Primary** — mark 66px + wordmark, 22px gap, on `#161B21` with 1px `#2A323C`, radius 20, padding 34/46.
  Wordmark: Space Grotesk **700 / 42px / 1**, `letter-spacing -.02em`, `#EAEEF3`.
  Descender line: Space Grotesk **600 / 13px**, `letter-spacing .34em`, uppercase, teal — `SQL EDITOR`.
- **Reverse** — mark 60px `markInk` + wordmark `#0F1319` on a `#35D0BE` tile, radius 20.
- Minimum clear space around the lockup = the mark's bore diameter (30 units at the mark's scale). Never re-color the wordmark to a signal color; never place `markDuo` on teal (use `markInk`).

## Typography
- **Brand:** Space Grotesk 400/500/600/700 (Google Fonts). Wordmark 700 `-.02em`; eyebrow/section labels 600, 11–13px, uppercase, `letter-spacing .14–.34em`, `#4E5865` on dark.
- **App UI:** system font (Segoe UI / San Francisco) — Space Grotesk is brand-surface only (splash, about, marketing, installer), not chrome.
- **Code:** monospace (`Cascadia Code` / `SF Mono` / `Consolas`).

## Palette
**Graphite · surfaces** — `ink-900 #0F1319` · `ink-800 #161B21` · `ink-700 #1A2027` · `ink-600 #222831` · `line #2A323C`
**Steel · text** — `steel-50 #EAEEF3` · `steel-100 #D8DEE6` · `steel-300 #B7C0CB` · `steel-500 #79838F` · `steel-700 #4E5865`
**Teal · brand** — `teal-light #5FE0D0` · `teal #35D0BE` · `teal-deep #1F9E90`
**Signal · status & syntax** — `rose #E76A86` · `gold #E3B457` · `mint #5FC9AD` · `azure #6FA6E2` · `violet #978BE4` · `amber #E9A46B`

Rules: exactly **one** teal accent in the product chrome — teal means *app state* (active nav, unsaved, current line, selection). Environment identity uses rose/gold/mint and never teal. Signal colors are functional only; don't use them decoratively.

## Files
- `Bearing - Logo.dc.html` — the brand sheet: mark variants, app-icon tiles, lockups, size ladder, monochrome, full palette.
