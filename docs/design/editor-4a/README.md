# Handoff: Squirrel — SQL Query Editor (Editor 4a)

## Overview
Squirrel is a cross-platform desktop SQL query editor (think DataGrip / TablePlus). This handoff covers the **main editor window** and its four alternate side-panel/overlay states: schema browser, scripts (with folders), query history, connection/database selectors, and a distraction-free focus mode.

The defining product idea: **the active database connection has a color** (production = red, staging = amber, local = teal). That color is threaded through the whole UI — the query tab, the tab list dot, the schema-tree connection dot, the results tab accent, and a **3px line across the very bottom status bar** — so a user always knows at a glance whether they're pointed at a dangerous environment.

## Target stack
- **.NET desktop app using Avalonia UI** (XAML + MVVM).
- Recreate the design in Avalonia's control set and styling system (`Styles`/`ControlTheme`, `DynamicResource` brushes). Do **not** port the HTML/CSS.
- Prefer MVVM: a `MainWindowViewModel` holding the active connection, active side-panel, dropdown state, and focus flag; `ObservableCollection`s for schema/scripts/history.

## About the design files
The file in this bundle (`Squirrel - Editor 4a.dc.html`) is a **design reference built in HTML** — a prototype showing intended look and behavior. It is **not** production code to copy. Recreate its appearance and interactions in the Avalonia app using the project's existing controls, styles, and MVVM patterns.

## Fidelity
**High-fidelity.** Colors, spacing, typography, and interaction states below are final. Match them. The one intentionally loose area is real syntax highlighting and grid virtualization — use the codebase's editor/data-grid controls (see Components).

---

## Design tokens

### Palette (Kanagawa-style dark theme)
| Token | Hex | Use |
|---|---|---|
| `bg.window` | `#16161D` | Title bar, left rail, status bar, dropdown/menu surfaces, results header row |
| `bg.chrome` | `#181820` | Toolbar, side panels, tab strip |
| `bg.editor` | `#1F1F28` | Editor + results body, active tab fill, window body |
| `bg.line-active` | `#252535` | Current-line highlight in editor gutter/row |
| `bg.tile-active` | `#2A2A37` | Active rail-icon tile, active pill/toggle |
| `bg.select` | `#223249` | Selected list row (schema/scripts/history/menu) |
| `border` | `#2A2A37` | 1px separators between regions |
| `border.control` | `#363646` | Input / button / dropdown borders |
| `text.primary` | `#DCD7BA` | Primary text |
| `text.code` | `#C8C093` | Code / monospace values |
| `text.muted` | `#9d967f` | Secondary tree rows |
| `text.dim` | `#727169` | Labels, captions |
| `text.faint` | `#54546D` | Line numbers, gutter, disabled glyphs |
| `accent.orange` | `#FF9E3B` | Active rail icon, unsaved dot, active toggle, active history border |
| `syntax.keyword` | `#957FB8` | SQL keywords / FK badge |
| `syntax.func` | `#7E9CD8` | Functions / server icon |
| `syntax.number` | `#FFA066` | Numeric literals |
| `syntax.table` | `#6A9589` / `#7AA89F` | Table icons (idle / selected) |
| `ok.green` | `#98BB6C` | Run icon, timings, DEFAULT badge, success |
| `error.red` | `#C34043` | Error history rows |

### Connection colors (drive the whole theme)
| Env | Color | Host label | DB |
|---|---|---|---|
| production | `#E46876` | `prod-db-01` | `pagila` |
| staging | `#E6C384` | `staging-db` | `pagila` |
| local | `#7AA89F` | `localhost` | `pagila` |

Expose the active connection color as a single `DynamicResource` brush (e.g. `ConnectionBrush`) so tab accent, dots, results accent, and the status-bar line all bind to it and recolor together when the connection changes.

### Typography
- **UI:** system font (Segoe UI on Windows / San Francisco on macOS). Sizes: labels 11px uppercase w/ `letter-spacing ~0.08–0.1em` (700), body 12.5–13px, tab/heading 13px 600.
- **Code & data:** monospace (`Cascadia Code`, `SF Mono`, `Consolas`). Editor 13.5px / line-height 1.62. Focus-mode editor 15px / 1.85. Grid & tree values 12–12.5px.

### Geometry
- Radii: controls/buttons/dropdown rows `6–7px`; rail tiles `9px`; dropdown/menu panels `8–9px`.
- Region heights: title bar **36px**, toolbar **46px**, tab strip **40px**, results dock **288px**, status bar auto (~28px).
- Left rail **52px** wide; icon tiles **36×36**. Side panels **262px** wide.
- Dropdown/menu shadow: `0 20px 50px -12px rgba(0,0,0,.75)`.

---

## Window layout
Vertical stack:
1. **Title bar** (36px) — macOS traffic-light dots (`#E46876`/`#E6C384`/`#98BB6C`), centered title `🐿 Squirrel — analytics` (`#727169`), right-aligned `⌥` hint.
2. **Toolbar** (46px) — see below.
3. **Body** (fills) — horizontal: left rail (52) · side panel (262, swaps by screen) · editor column (fills).
4. **Status bar** — 3px top border in `ConnectionBrush`, then a row of muted metadata.

Overlays (absolute, above everything): server dropdown, database dropdown, Alt menu bar+File menu, and full-screen focus mode.

### Toolbar (left → right)
- **Proj** selector pill: label `PROJ` + `analytics` + `▾`. `bg.editor`, `border.control`, radius 7, padding 6/10.
- 1px divider.
- **Server** selector pill: server icon (`#7E9CD8`) + host label + `▾`. Opens server dropdown; border turns `#7E9CD8` while open.
- `›` separator.
- **Database** selector pill: db icon (`#98BB6C`) + db name (600) + **connection-color dot** + `▾`. Opens db dropdown; border turns `#98BB6C` while open.
- **Run** button — small **30×30** icon-only, transparent bg, `border.control`, green `▶` glyph. Tooltip `Run ⌘⏎`. (Deliberately understated — not a big labeled button.)
- Spacer.
- `Alt menu` hint (`kbd` chip).
- **Focus** button — 30×30, `⤢` glyph, tooltip `Focus mode ⌃⌘F`.

---

## Screens / states

### 1. Left rail (persistent nav)
Vertical icon column. Buttons: Connections `🖧`, Schema `🗄`, Scripts `📄`, History `🕘`, spacer, Settings `⚙`.
- **Idle:** transparent tile, `#727169` glyph. **Hover:** tile `#20202A`.
- **Active:** tile fill `bg.tile-active` (`#2A2A37`), glyph `accent.orange`. Full rounded tile — **no left-edge border/indicator** (explicitly rejected earlier).
Clicking Schema/Scripts/History swaps the 262px side panel.

### 2. Schema panel (default)
Header `SCHEMA` + search glyph. Monospace tree, line-height ~1.95:
- Connection node (server icon + host) with connection-color dot right-aligned.
- Database node (db icon + name bold) + green `DEFAULT` badge.
- `public` schema → tables (`actor`, `film` [expanded, selected → `bg.select`], `rental`, `inventory`).
- Under `film`: columns as three-part rows `name | badge | type` — `film_id` PK (orange badge) `int4`, `title varchar`, `language_id` FK (`#957FB8` badge) `int2`, `rental_rate numeric`, then `▸ Indexes (3)`.
Disclosure triangles `▾`/`▸` in `#54546D`. Indentation via left padding steps.

### 3. Scripts panel (folders)
Header `SCRIPTS` + new-folder `🗀` + new-script `＋`. Filter input (`⌕ Filter scripts…`, `bg.window`, `border`, radius 6).
Folder tree, line-height ~2.05:
- `🗀 Reports` (count 4, right-aligned) → `monthly_rentals.sql`, `top_films.sql` [selected `bg.select` + orange unsaved dot], `revenue_by_store.sql`, `customer_ltv.sql`.
- `🗀 Migrations` (2) → `001_add_indexes.sql`, `002_seed_data.sql`.
- `🗀 Ad-hoc` (11, collapsed).
- Ungrouped `scratch.sql`.
Folder glyph amber `#E6C384`; file glyph `#727169` (orange when active). Counts in `text.faint`.

### 4. History panel
Header `HISTORY` + search. Filter pills row: `All` (active pill `bg.tile-active`), `✓ ok`, `✕ error` (idle `text.dim`).
Grouped by day (`TODAY`, `YESTERDAY` — faint uppercase captions). Each row: connection-color dot · truncated monospace query (ellipsis) · right-aligned time.
- Selected row: `bg.select` + **2px left border `accent.orange`**.
- Error rows: text `error.red`, query prefixed `✕`.
Sample data — Today: `select f.title, count(*)…` 14:22 (red dot, selected); `select * from film limit…` 14:20; `✕ update rental set…` 13:58 (error, teal dot); `explain analyze select…` 11:04 (amber dot). Yesterday: `select store_id, sum…` 18:31; `select count(*) from…` 16:12.

### 5. Editor column (shared across screens)
- **Tab strip** (40px, `bg.chrome`): active tab = `bg.editor` fill, **2px top border in `ConnectionBrush`**, radius 7 7 0 0; contents `top_films.sql` (600) + orange unsaved `•` + connection dot + connection name label **in connection color** + `✕`. Second tab `scratch.sql` with teal `local` label. Trailing `+`.
- **Editor body** (`bg.editor`): gutter (44px, right-aligned line numbers `text.faint`) + syntax-highlighted SQL. Current line row uses `bg.line-active` and an orange line number. Keywords `#957FB8`, functions `#7E9CD8`, numbers `#FFA066`. Use the codebase's code-editor control (e.g. AvaloniaEdit) with a matching TextMate/highlight theme rather than hand-spanning tokens.
- **Results dock** (288px, top `border`): header row `RESULTS` + `Stacked`/`Tabbed` segmented toggle (active segment `accent.orange` fill, `#1F1F28` text). Result meta row: `▾ Result · top rentals · 10 rows · 88 ms` + `⭳ Export` outline button. Data grid: header row `bg.window`/`text.dim`/600 with column dividers; body rows `text.primary`, 1px `#252531` row separators, first column dividers `#252531`. Sample columns `title | rentals`. Use the codebase's DataGrid (virtualized) for real data.

### 6. Connection & database dropdowns (selector active)
Anchored under their toolbar pills; click-catcher backdrop closes them.
- **Server dropdown** (280px): `CONNECTIONS` caption, then one row per connection — env-color dot + bold host + `env · postgres <ver>` subtitle; active row `bg.select` + green `✓`. Divider, then `＋ New connection…` (`syntax.func`). Rows hover `#20202A`.
- **Database dropdown** (250px): search row, divider, db rows — db icon + name; active `bg.select` + `DEFAULT` badge + `✓`. Icons green when active, `#6A9589` idle. Items: `pagila` (default), `analytics`, `warehouse`, `postgres`.
In Avalonia use `Popup`/`Flyout` anchored to the pill; toggle border-highlight on the pill while open.

### 7. Focus mode
Full-window overlay (`bg.editor`, above all).
- Slim 34px top bar: `top_films.sql` + unsaved `•` + connection dot & name; right side small `▶` run + `⤡ Exit` button with `Esc` kbd hint.
- Centered editor column, **max-width 820px**, larger type (15px / 1.85), generous vertical padding; same syntax colors and current-line highlight.
- **3px `ConnectionBrush` line pinned to the bottom.**
No rail, no side panel, no results — pure editing.

---

## Interactions & behavior
- **Left-rail click** → sets active side panel (schema/scripts/history). Active tile styling as above.
- **Toolbar server/database pill click** → toggles the corresponding dropdown; opening one closes the Alt menu; clicking the backdrop or choosing an item closes it; open pill shows colored border.
- **Selecting a connection** → updates `ConnectionBrush` app-wide (tab accent, all dots, results accent, status line, focus line recolor together) and the status-bar/schema host & env labels.
- **Alt key** → toggles the menu bar (`File/Edit/View/Query/Help`) + File flyout (New Query ⌘N, Open… ⌘O [highlighted], Open Recent ▸, Save ⌘S, Save As… ⇧⌘S, Close Tab ⌘W). **Open and Save live only in this menu — they are not on the toolbar.** Menu hidden by default.
- **Focus button / Ctrl+⌘F** → enter focus mode. **Esc** exits focus mode and also closes any open menu/dropdown.
- **Run** (`⌘⏎`) executes the current statement (understated icon button in both toolbar and focus bar).
- Hover states: rail tiles `#20202A`; dropdown/menu rows `#20202A` (or `bg.select` for the pre-selected row).

## State (MVVM)
- `ActiveConnection` (enum/object → drives `ConnectionBrush`, host, db, env label).
- `ActivePanel` (`Schema | Scripts | History`).
- `OpenDropdown` (`None | Server | Database`).
- `IsMenuVisible` (Alt toggle).
- `IsFocusMode`.
- Collections: `Schema` tree, `ScriptFolders` (folder → scripts, with unsaved flag), `History` (grouped by day, with status + connection color + timestamp), `OpenTabs` (name, unsaved, connection).
- Cursor position (`Ln 4, Col 24`) and result meta (rows, ms) for status/results.

## Assets
No image assets. Icons are inline SVG in the prototype (server = stacked rectangles, database = cylinder, table = grid rectangle) — replace with the project's icon set (e.g. Fluent/Lucide Avalonia icons) or vector `PathIcon`s. Traffic-light dots and the squirrel emoji are decorative; use the app's real window chrome and app icon.

## Files
- `Squirrel - Editor 4a.dc.html` — the high-fidelity interactive prototype (all states above). Open in a browser; use the left rail, toolbar selectors, Alt key, and Focus button to reach each state.
