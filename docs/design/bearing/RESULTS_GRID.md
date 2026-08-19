# Handoff: Bearing — Results grid

Companion to `README.md`. Specifies the **results dock** — the panel below the query editor. Same target (**.NET / Avalonia, MVVM**) and tokens as the parent handoff. Reference prototype: `Bearing - Editor.dc.html` (design reference, not production code).

---

## 1. Dock structure (top → bottom)
Fixed-height **322px** column pinned under the editor, `ink-700` (`#1A2027`), 1px `#2A323C` top border. Five stacked regions:

1. **Dock header** — `RESULTS` (11px uppercase, `#79838F`, 700) + right-aligned **Stacked / Tabbed** toggle. Always visible.
2. **Result meta + controls row** — identity, timing, edit/action controls. `flex-wrap` so nothing clips when crowded.
3. **Grid + inspector** — horizontal split: scrollable grid (fills) + 400px inspector pane when a cell is being inspected.
4. **Selection quick-stats bar** — only when ≥2 numeric cells are selected.
5. **Paging footer** — row count, count-on-demand, load more.

Meta row content: `▾ Result` (600) · `public.film` (`#79838F`) · `·` · `42 ms` (mint).

---

## 2. Stacked vs. Tabbed
A batch can return multiple result sets; the toggle is a persistent user preference.
- **Stacked** (default): sets render vertically in one scroll area, each with its own meta row + collapse chevron (`▾`/`▸`).
- **Tabbed**: one set visible; others become tabs across the top of the results body.

Toggle: segmented control on `ink-900`, 1px `#2A323C`, radius 7, 2px padding; active segment `#2A323C` fill with `#D8DEE6` text, inactive `#79838F`. Glyphs `▤ Stacked` / `▭ Tabbed`. Implementation: `ResultSets` collection + `ResultsViewMode` enum driving an `ItemsControl` vs. a `TabControl`. The prototype shows one set — build for N.

---

## 3. Grid columns & layout
Scrolls horizontally and vertically; header sticks.

| Col | Width | Notes |
|---|---|---|
| `#` row number | 46px | Styled like the header (`ink-900`, 600, `#79838F`), right-aligned, right border. Ordinal of the loaded row, not data. Green (`#5FC9AD`) for new rows. |
| `film_id` | 88px | **PK** — teal `PK` badge in header. Read-only, `#4E5865`. |
| `title` | 210px | Editable text (inline input). |
| `language_id` | 110px | **FK** — violet `FK` badge; values violet. |
| `release_year` | 104px | Numeric, `#B7C0CB`. Selectable (§7). |
| `rental_rate` | 104px | Numeric, editable. Selectable (§7). |
| `metadata` | 250px | `jsonb` — teal-green (`#6BAEA2`) badge; truncated raw JSON in `#6FB6AB` + `⤢` inspect affordance (§6). |
| (row actions) | 30px | Delete/undo, revealed on row hover. |

Header row: `ink-900`, `#79838F`, 600, sticky, 1px `#2A323C` column dividers; type badges inline after the name at 9px/700. Body rows: monospace 12px, `#D8DEE6`, 1px `#232A33` separators, zebra tint `rgba(255,255,255,.022)` on odd rows. Cell inputs are borderless and inherit; focused input gets `#232B36` + a 1px azure inset ring, radius 3.

---

## 4. Row editing model
Editable: `title` (always inline), `rental_rate` (read-only until **double-clicked** — single click selects for stats, §7). Row status drives a 2px **left bar** and a faint row tint:
- **Edited** — teal `#35D0BE` bar.
- **New** — mint `#5FC9AD` bar + mint row number, tint `rgba(152,187,108,.07)`; inserted via **＋ Add row**.
- **Deleted** — red `#D2555A` bar, tint `rgba(195,64,67,.07)`, strikethrough; toggled by the row glyph `✕` → `↺`. New rows are removed outright instead of marked.

**Entering NULL.** `(null)` is both how a NULL cell renders and the token you type to set one (trimmed, case-insensitive) — but typing it is not the discoverable path, so the grid offers **Set NULL** in the right-click menu and as `grid.setNull` (`Ctrl+Shift+N`, palette-reachable). It writes over the whole selection, skips cells in NOT NULL columns (reporting how many), and is the only way to NULL a checkbox cell, which has no text editor. Note that **clearing** a cell is not the same thing: an empty editor saves `''` on a text column and NULL on every other type.

### Commit bar (only when changes are pending)
In the meta row, left of the always-present `＋ Add row` / `⭳ Export`:
`● N pending` (teal dot + count) · `‹ › Script` (outline) · `Discard` (red text, `#3a2530` border) · `✓ Save changes` (mint fill, `#0F1319` text) · divider.

State: `Rows` (`Id, FilmId, Title, Year, Rate, Lang, Meta, IsNew, IsDirty, IsDeleted`), `EditingCell`. Pending set is derived, not stored.

---

## 5. Pending changes as SQL script
Floating panel, 520px wide / 340px max height, bottom-right (`right:20 bottom:52`), dim backdrop, `ink-900`, 1px `#333C48`, radius 10, shadow `0 24px 60px -16px rgba(0,0,0,.8)`. Line-numbered statements, color-coded: **INSERT** mint · **UPDATE** gold · **DELETE** red. Header: `Pending changes · N statement(s)` + `⧉ Copy` + `✕`. Footer: `Discard` + `✓ Run & save`.

Generation in the prototype is naive by design (title/rate in the UPDATE, film_id in WHERE). Real impl: parameterized statements from the connection's dialect + the result's key metadata.

---

## 6. Cell inspector
Opens as a **400px right pane** (`ink-800`, 1px left border) when a `metadata` cell or its `⤢` is clicked; the inspected cell gets a `rgba(126,156,216,.12)` tint.

**JSON (`jsonb`)** — *Formatted*: syntax-highlighted tree, keys azure, strings mint, numbers `#E9A46B`, bool/null violet; every object/array node has a fold triangle (`▾`/`▸`), collapsed nodes show `{…N…}` / `[…N…]`; toolbar `⊟` collapse-all / `⊞` expand-all. *Raw*: unformatted single-line value. Indent 16px per depth.
**Text** — multiline preserved (`pre-wrap`); badge reads `text`.
**Find in value** — live search across keys and values; matches highlighted `#294a45` / `#EAEEF3`.

Header: `film[<id>].<column>` (monospace 600) + type badge + `⧉ Copy` (pretty value) + `✕`. Formatted/Raw segmented toggle: active segment teal fill with `#1A2027` text.

State: `Inspect {RowId, Col}`, `JsonMode {Formatted|Raw}`, `InspectorSearch`, `Collapsed` (node paths). In Avalonia back this with a `TreeView` + custom item template rather than a hand-rolled tree.

---

## 7. Numeric selection → quick stats
- Selectable: `release_year`, `rental_rate` only. **IDs and FKs are deliberately excluded** — summing keys is meaningless.
- **Click** selects one cell; **Cmd/Ctrl/Shift-click** adds/removes. Selected cells get tint `rgba(126,156,216,.18)` + an azure inset ring drawn only on the *outer* edges of a contiguous selection block. Cursor `cell`.
- At **≥2** selected cells the stats bar appears above the footer (`#222831`, 11.5px): `N cells · count · sum · avg · min · max` + right-aligned `Clear`. Sum in mint; values rounded to ≤2 dp, locale-grouped.

Note the split on `rental_rate`: single-click selects, double-click edits. State: `Selected` (set of `rowId:col`); stats derived.

---

## 8. Read-only / lock state
When the result isn't safely editable (no PK, join/aggregate, non-simple query):
- The `#` header cell shows 🔒 in gold with a `help` cursor and a tooltip explaining why (default: *"Read-only: no primary key found on this result — can't generate a safe UPDATE."*).
- All inputs go read-only; **Add row** and per-row delete are hidden.
- Inspector, selection/stats and paging remain available.

Driven in the prototype by the `gridEditable` (bool) + `lockReason` (string) tweaks; in the app derive from the result's key/query metadata.

---

## 9. Paging
- Loads the **first 100 rows**; **total is not shown by default** (avoids `COUNT(*)` on big tables).
- Footer (`ink-800`, 11.5px, `#79838F`): `Showing <n> rows`. A **∑** azure icon button fetches the total on demand → `Showing 100 of 1,000`, then the button disappears. **↓ Load more** appends the next page (infinite-scroll model, no page numbers).

State: `Loaded` (int), `Total` (nullable; null = not counted), `GetCount()`, `LoadMore()`.

---

## 10. Files
- `Bearing - Editor.dc.html` — prototype. To exercise the grid: scroll right to `metadata` and click `⤢`; single/Cmd-click `rental_rate` / `release_year` cells; double-click a rate to edit; edit/add/delete rows to reveal the commit bar and script panel; use the footer for count and load-more; toggle `gridEditable` in Tweaks for the lock state.
