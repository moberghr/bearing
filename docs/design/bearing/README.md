# Handoff: Bearing — SQL editor redesign

> **Repo note — what was actually implemented (not part of the handoff).**
> The 2026-08-04 pass took the palette, the mark, and the name — including a **full code rename**
> (`Squirrel.*` → `Bearing.*` namespaces/assemblies/projects, `SQUIRREL_*` → `BEARING_*` env vars,
> and `AppDirName` → `bearing`). Colour *semantics* already established in the app were deliberately
> kept, so a few things here are **intentionally not implemented** — don't "fix" them without asking:
>
> | Handoff says | This repo does | Why |
> |---|---|---|
> | Connected status dot takes the environment colour (§CONNECTION_STATUS 1) | Dot stays semantic: **green** Connected / gold Connecting / grey *hollow ring* Disconnected | Status colour must indicate *status*. What #45 changed is that the environment left the dot vocabulary entirely — see below |
> | `Ok.Green` becomes mint `#5FC9AD` | Kept at `#98BB6C` | Mint is too close to `Accent.Brand` teal — the Run glyph stopped reading as its own signal against the brand chrome |
> | Editor tab carries both an unsaved dot *and* an env dot + env name | Teal unsaved dot + a chain-glyph chip washed in the connection colour; no env dot, no env name | Follows the prototype's tab strip (#45): the dot means unsaved, the chain means connected, and the environment is the chip's fill rather than tinted text |
> | Environment presets rose/gold/mint (§README Connection colors) | Presets keep `#3FB950`/`#D29922`/`#E5484D` | Re-hueing them would leave saved connections on old hexes and new ones on new hexes |
>
> **#45 (2026-08-22) — the environment left the dot vocabulary; the dot kept green.** The deviation above
> originally justified itself with *"the environment is already signalled plenty of other ways"*. In use
> that didn't hold, because those other ways were **more dots**: a green env dot read as "connected" and a
> red one as "disconnected" two rows away — the "competing green/red dots" §CONNECTION_STATUS 1 predicted.
>
> The spec's own answer (Connected takes the environment colour) was implemented and then **backed out**: a
> live production session rendering as a red dot beside the word "Connected" is a worse signal than the
> collision it solved. What fixed the collision instead was removing the *other* side of it. The environment
> is no longer a dot anywhere in the toolbar or tab strip — it is a fill, an edge, a chip and the status-bar
> line — so green and red in this chrome are unambiguously status.
>
> State therefore keeps green Connected / gold Connecting, and `Error.Red` still left the vocabulary:
> Disconnected is a grey **hollow ring**, since a connection that simply isn't open yet is not an error, and
> hollow-vs-filled means "not live" reads by shape as well as hue.
>
> The toolbar **server control** changed with it, since that is where the collision was actually seen:
> its 9px environment dot is gone and the whole control became the environment, matching the design
> prototype's toolbar — filled with the active connection's colour at 16%, outlined in it, with the azure
> server glyph (`Icon.Connections` / `Syntax.Func`) where the dot used to be. Each dropdown row is filled
> with its own environment the same way. The row is now the icon plus the connection name only — the host
> moved to the row's tooltip — and the name stays in normal text colour. A filled control cannot be read as
> a status light. Environment dots elsewhere (schema-tree server node, history rows) are
> left alone: they are no longer ambiguous now that no state is a green or red dot.
>
> The **editor tab strip** followed: the environment-coloured connection *name* is gone, replaced by the
> prototype's chip — the tab's environment colour at 16% carrying a chain glyph, green and linked when that
> tab's connection has a live session, grey and broken when it does not, with the connection name as its
> tooltip. Fill is the environment, glyph is the state: two channels, no collision. The
> tab's other dot keeps its own job (teal = unsaved). This needed real per-tab state:
> `EditorTabViewModel.ConnectionLive`, kept in sync by `ConnectionsViewModel.RefreshTabConnectionLive` off
> the session pool's `LiveChanged`, because the strip shows every tab's connection and not just the
> selected one's — two tabs on the same connection *and database* link and break together, a tab on another
> server does not. (At the time of this pass one server meant one session; see the #54 note below.)
>
> The **Connections pane** matches: its server row's 9px environment dot became a wash across the whole
> row (`SchemaNodeViewModel.RowAccentColor`, applied to the `TreeViewItem` background so it covers the
> expander too), with a chain glyph docked right for state. That glyph is deliberately *coarser* than the
> tab chip's — the node is the server, so any live session on that connection counts whichever database it
> is open on, since the databases are the node's own children.
>
> Both server glyphs (`Icon.Connections`, in the toolbar dropdown and the pane) sit 2px lower than centre:
> `Stretch="Uniform"` centres the ink in its box, and a box centred against a text line reads optically
> high against the letterforms.
>
> One accuracy note that came out of this: the indicators are per **(connection, database)**, not per
> connection — `ConnectionsViewModel.IsTabSessionLive` requires the live session to be open on the database
> the tab targets. Sessions were then keyed by connection Id alone (§9.4), so a tab on another database of
> the same connection genuinely had no pool and now said so, and switching database visibly disconnected.
> #54 (below) fixed the keying; the indicator logic it forced is unchanged and now simply reads one key.
>
> **The environment presets keep `#3FB950` / `#D29922` / `#E5484D`** rather than moving to the handoff's
> rose/gold/mint (that half of the original deviation stands — re-hueing would split saved connections
> across two palettes). Safe to keep, now that no environment colour lands on a dot.
>
> On the data directories: `BearingPaths.AppDirName` is now `bearing`, so the app reads
> `~/.config/bearing` and `~/.local/share/bearing`. **No migration code was written — a deliberate
> call.** Existing installs move their data by hand (README ▸ Upgrading from Squirrel); keyring
> secrets stored under `app=squirrel` are not visible under `app=bearing` and need re-entering.
> Still on the old name: the repo directory and git remote.
>
> Scope of the pass: **colour, mark and name only.** No layout, geometry or control was rebuilt.
> The results-dock behaviour `RESULTS_GRID.md` describes largely predates this handoff and already
> exists (`Results/CellStats.cs` §7, `LoadMore`/`HasMore` §9, `ResultSetViewModel.LockReason` §8,
> `Controls/ResultView.Inspector.cs` §6) — it was re-skinned, not re-implemented. Where this file
> prescribes sizes and orderings that differ from the current shell, those remain design targets.

> **#54 (2026-08-22) — a database switch stopped disconnecting.** The visible disconnect #45 started
> reporting honestly is gone, because the thing it was reporting is fixed. `ConnectionSessionManager` now
> keys live sessions on `SessionKey` — connection id **and** database — so a tab pointed at another database
> on the same server gets its own pool instead of tearing down the neighbour's, and switching back and forth
> reuses both. Nothing about the indicators changed: `IsTabSessionLive` is now a single keyed lookup and
> answers the same question it did before.
>
> The decision the re-keying forced was what eviction means. Disconnect on the toolbar chain drops **every**
> database on the connection (`EvictConnectionAsync`), not just the selected tab's — the button says
> "disconnect from server", and the Connections pane's server row lights for any live session on the
> connection, so a one-database evict would have left that row linked immediately after the user pressed
> Disconnect. Connection edited / deleted / metadata-refreshed and project close are server-level too;
> only a cancelled connect and a credential-refresh retry evict a single database.
>
> Second-order: one pool per (connection, database) instead of per connection, so `NpgsqlConnectionFactory`
> now sets `MaxPoolSize = 10` rather than inheriting Npgsql's default of 100 for each.

## Overview
**Bearing** is the rebrand + redesign of the Squirrel desktop SQL query editor (think DataGrip / TablePlus). Two things change together:

1. **Brand** — new name, new mark (a ball bearing: precision, low friction, engineered), new palette. Graphite surfaces, steel text, one machined-teal accent. See `BRAND.md`.
2. **Product UI** — the whole editor window re-skinned onto the new palette, plus the results dock (editable grid, JSON cell inspector, quick stats, paging) and the connection status control. See this file, `RESULTS_GRID.md`, `CONNECTION_STATUS.md`.

The defining product idea is unchanged: **the active database connection has an environment color** (production = rose, staging = gold, local = mint). That color threads through the editor tab accent, tab-list dots, schema-tree connection dot, and a **3px line across the bottom status bar** — so the user always knows whether they're pointed at something dangerous. The brand teal (`#35D0BE`) is used for *app* state (active nav, unsaved dot, current line, selection ring) and never for environment.

## Target stack
- **.NET desktop app using Avalonia UI** (XAML + MVVM).
- Recreate the design in Avalonia's control set and styling system (`Styles`/`ControlTheme`, `DynamicResource` brushes). Do **not** port the HTML/CSS.
- MVVM: a `MainWindowViewModel` holding active connection, connection state, active side panel, dropdown state, focus flag; `ObservableCollection`s for schema / scripts / history / result rows.

## About the design files
`Bearing - Editor.dc.html` and `Bearing - Logo.dc.html` in this bundle are **design references built in HTML** — prototypes showing intended look and behavior. They are **not** production code to copy. Recreate their appearance and interactions in the Avalonia app using the project's existing controls, styles, and MVVM patterns. (If no environment exists yet, pick the framework that fits the project and implement there.)

## Fidelity
**High-fidelity.** Colors, spacing, typography, and interaction states below are final — match them. The intentionally loose areas: real SQL syntax highlighting (use AvaloniaEdit + a TextMate theme matching the syntax colors below) and grid virtualization (use the codebase's DataGrid).

---

## Design tokens

### Graphite · surfaces
| Token | Hex | Use |
|---|---|---|
| `ink-900` | `#0F1319` | Title bar, left rail, status bar, dropdown/menu surfaces, grid header, filter inputs |
| `ink-800` | `#161B21` | Toolbar, side panels, tab strip, inspector pane, paging footer |
| `ink-700` | `#1A2027` | Editor + results body, active tab fill, window body |
| `ink-600` | `#222831` | Rail/menu hover, quick-stats bar |
| `line` | `#2A323C` | 1px separators between regions; row separators use `#232A33` |
| `border.control` | `#333C48` | Input / button / dropdown-pill borders |
| `bg.select` | `#223440` | Selected list row (schema / scripts / history / menu / dropdown) |
| `bg.line-active` | `#232B36` | Current-line highlight in the editor; focused cell input |

### Steel · text
| Token | Hex | Use |
|---|---|---|
| `steel-50` | `#EAEEF3` | Display text on brand surfaces (logo sheet) |
| `steel-100` | `#D8DEE6` | Primary text |
| `steel-300` | `#B7C0CB` | Secondary / code values |
| `steel-500` | `#79838F` | Labels, captions, idle glyphs |
| `steel-700` | `#4E5865` | Line numbers, gutter, disabled glyphs, counts |
| (tree muted) | `#8b95a1` | Collapsed tree rows |

### Teal · brand accent
`teal-light #5FE0D0` · **`teal #35D0BE`** · `teal-deep #1F9E90`.
`#35D0BE` marks: active rail-icon glyph, unsaved-file dot, current-line number, PK badge, dirty-row bar, inspector toggle fill, history selected-row left border.

### Signal · status & syntax
| Token | Hex | Use |
|---|---|---|
| `rose` | `#E76A86` | production env color; traffic light |
| `gold` | `#E3B457` | staging env color; folder glyph; UPDATE statements; connecting state |
| `mint` | `#5FC9AD` | local env color (`#6FB6AB` when used as a tree/table stroke); run glyph, success, DEFAULT badge, timings, save button |
| `azure` | `#6FA6E2` | server icon, functions, JSON keys, links, selection ring |
| `violet` | `#978BE4` | SQL keywords, FK badge |
| `amber` | `#E9A46B` | numeric literals |
| `red` | `#D2555A` | errors, delete/discard |

### Connection (environment) colors
| Env | Color | Host | DB |
|---|---|---|---|
| production | `#E76A86` | `prod-db-01` | `pagila` |
| staging | `#E3B457` | `staging-db` | `pagila` |
| local | `#6FB6AB` | `localhost` | `pagila` |

Expose the active connection color as one `DynamicResource` brush (e.g. `ConnectionBrush`) so tab accent, dots, and the status-bar line recolor together.

### Typography
- **Brand / marketing:** **Space Grotesk** (400–700). Wordmark 700, `letter-spacing -.02em`; eyebrow labels 600, 11–13px, `letter-spacing .28–.34em`, uppercase.
- **App UI:** system font (Segoe UI / San Francisco). Section labels 11px uppercase 700 `letter-spacing .1em`; body 12–13px; tab/heading 13px 600.
- **Code & data:** monospace (`Cascadia Code`, `SF Mono`, `Consolas`). Editor 13.5px / 1.62; focus mode 15px / 1.85; grid & tree 12–12.5px.

### Geometry
- Radii: controls/pills/dropdown rows `6–7px`; rail tiles `9px`; panels `8–10px`; app icon tile `56px @ 256px` (≈22%).
- Heights: title bar **36px**, toolbar **46px**, tab strip **40px**, results dock **322px**, status bar ~28px, focus top bar **34px**.
- Left rail **52px**, icon tiles **36×36**. Side panels **262px**. Inspector **400px**. Script panel **520px**.
- Panel shadow `0 20px 50px -12px rgba(0,0,0,.75)`.

---

## Window layout
Vertical stack:
1. **Title bar** (36px, `ink-900`) — traffic-light dots (`#E76A86`/`#E3B457`/`#5FC9AD`), centered **bearing mark (15px) + “Bearing — analytics”** in `#79838F`, right-aligned `⌥` hint.
2. **Toolbar** (46px, `ink-800`).
3. **Body** — left rail (52) · side panel (262, swaps) · editor column (fills).
4. **Status bar** — 3px top border in `ConnectionBrush`, then connection state, `db @ host`, table count, and right-aligned `Ln 4, Col 24 · UTF-8 · PostgreSQL 16.2`.

Overlays (absolute): server dropdown, database dropdown, Alt menu bar + File menu, pending-changes script panel, full-screen focus mode.

### Toolbar (left → right)
`[PROJ analytics ▾] │ [🖧 host ▾]  ● Connected  [⛓]  ›  [🗄 db ● ▾]  [▶]  ……  Alt menu  [⤢]`
- Pills: `ink-700` fill, 1px `#333C48`, radius 7, padding 6/10. Server pill border turns `#6FA6E2` while its dropdown is open; database pill `#5FC9AD`.
- Database pill carries the **environment dot** right of the name.
- **Run** is a deliberately understated 30×30 icon button (mint `▶`, tooltip `Run ⌘⏎`) — not a big labeled button.
- **Focus** 30×30 `⤢`, tooltip `Focus mode ⌃⌘F`. Connection toggle: see `CONNECTION_STATUS.md`.

---

## Screens / states

### 1. Left rail (persistent nav)
Icons: Connections `🖧`, Schema `🗄`, Scripts `📄`, History `🕘`, spacer, Settings `⚙`. Idle: transparent tile, `#79838F` glyph. Hover: `#222831`. Active: tile `#2A323C`, glyph **teal `#35D0BE`** — full rounded tile, **no left-edge indicator**. Clicking Schema/Scripts/History swaps the 262px panel.

### 2. Schema panel (default)
Header `SCHEMA` + `⌕`. Monospace tree, line-height 1.95:
connection node (azure server icon + host + env dot right-aligned) → database node (mint db icon + bold name + mint `DEFAULT` badge) → `▾ public` → tables `actor`, **`film`** (expanded, selected `#223440`), `rental`, `inventory`. `film` columns render as `name | badge | type`: `film_id` **PK** (teal) `int4`, `title varchar`, `language_id` **FK** (violet) `int2`, `rental_rate numeric`, then `▸ Indexes (3)`. Disclosure triangles `#4E5865`; indentation by left-padding steps (4/20/44/56/78px).

### 3. Scripts panel
Header `SCRIPTS` + `🗀` new folder + `＋` new script. Filter input (`⌕ Filter scripts…`, `ink-900`, 1px `line`, radius 6). Folder tree, line-height 2.05: `🗀 Reports` (4) → `monthly_rentals.sql`, **`top_films.sql`** (selected + teal unsaved dot), `revenue_by_store.sql`, `customer_ltv.sql`; `🗀 Migrations` (2) → `001_add_indexes.sql`, `002_seed_data.sql`; `🗀 Ad-hoc` (11, collapsed); ungrouped `scratch.sql`. Folder glyph gold; file glyph `#79838F` (teal when active); counts `#4E5865`.

### 4. History panel
Header `HISTORY` + `⌕`. Filter pills `All` (active, `#2A323C`), `✓ ok`, `✕ error`. Grouped by day (`TODAY`, `YESTERDAY` — 10.5px uppercase `#4E5865`). Row = env dot · truncated monospace query · right-aligned time. Selected row: `#223440` + **2px left border teal**. Error rows: text `#D2555A`, query prefixed `✕`.
Sample — Today: `select f.title, count(*)…` 14:22 (rose, selected); `select * from film limit…` 14:20; `✕ update rental set…` 13:58 (error, mint dot); `explain analyze select…` 11:04 (gold). Yesterday: `select store_id, sum…` 18:31; `select count(*) from…` 16:12.

### 5. Editor column
- **Tab strip** (40px, `ink-800`): active tab = `ink-700` fill, 1px `line`, **2px top border in `ConnectionBrush`**, radius `7 7 0 0`; contents `top_films.sql` (600) + teal unsaved `•` + env dot + env name **in env color** + `✕`. Second tab `scratch.sql` with mint `local`. Trailing `+`.
- **Editor body**: 44px right-aligned gutter (`#4E5865`), syntax-highlighted SQL, current line row `#232B36` with a **teal** line number. Keywords violet, functions azure, numbers `#E9A46B`.
- **Results dock** (322px) — full spec in `RESULTS_GRID.md`.

### 6. Server & database dropdowns
Anchored under their pills, `ink-900` panel, 1px `#333C48`, radius 9, backdrop click-catcher.
- **Server** (280px): `CONNECTIONS` caption, then env dot + bold host + `env · postgres <ver>` subtitle; active row `#223440` + mint `✓`; divider; `＋ New connection…` in azure. Hover `#222831`.
- **Database** (250px): search row, divider, db rows (mint icon when active, `#6BAEA2` idle); active row `#223440` + `DEFAULT` badge + `✓`. Items `pagila` (default), `analytics`, `warehouse`, `postgres`.
Use `Popup`/`Flyout` anchored to the pill; toggle the pill's border highlight while open.

### 7. Focus mode
Full-window `ink-700` overlay. 34px top bar: `top_films.sql` + teal unsaved `•` + env dot/name; right `▶` run (26×26) + `⤡ Exit` with `Esc` kbd hint. Centered editor column **max-width 820px**, 15px / 1.85, 26px vertical padding, 52px gutter. **3px `ConnectionBrush` line pinned to the bottom.** No rail, no panel, no results.

### 8. Alt menu
Alt toggles a 30px menu bar under the title bar (`File Edit View Query Help`, mnemonic underlines, active item `#2A323C`) plus the File flyout: New Query ⌘N · **Open… ⌘O** (highlighted) · Open Recent ▸ · — · Save ⌘S · Save As… ⇧⌘S · — · Close Tab ⌘W. **Open and Save live only here — never on the toolbar.** Hidden by default.

---

## Interactions & behavior
- Rail click → sets active side panel.
- Server / database pill click → toggles that dropdown, closes the Alt menu; backdrop or item click closes it.
- Selecting a connection → updates `ConnectionBrush` app-wide (tab accent, dots, status line, focus line) plus host/db/env labels.
- **Alt** toggles the menu; **Esc** closes menu, dropdowns and focus mode; **⌃⌘F** toggles focus; **⌘⏎** runs the current statement.
- Hover: rail tiles and menu/dropdown rows `#222831`; grid row hover reveals the delete affordance.

## State (MVVM)
`ActiveConnection` (→ `ConnectionBrush`, host, db, env label) · `ConnectionState` (`Connected|Connecting|Disconnected`) · `ActivePanel` (`Schema|Scripts|History`) · `OpenDropdown` (`None|Server|Database`) · `IsMenuVisible` · `IsFocusMode` · collections `Schema`, `ScriptFolders`, `History`, `OpenTabs`, `Rows` · results state per `RESULTS_GRID.md` · cursor position and result meta for the status bar.

## Assets
No raster assets. The **bearing mark is generated geometry** — see `BRAND.md` §Mark for exact construction (reproduce as a `PathIcon`/`DrawingImage` or ship an SVG; do not re-draw by eye). Toolbar/tree icons (server, database, table, chain) are simple inline SVGs — replace with the project's icon set (Fluent/Lucide) at matching stroke weight (2px on a 24 viewBox). Rail and folder glyphs are emoji in the prototype — swap for real icons.

## Files
- `BRAND.md` — mark construction, lockups, app-icon tiles, full palette, type.
- `RESULTS_GRID.md` — results dock: grid, row editing, SQL script panel, cell inspector, quick stats, lock state, paging.
- `CONNECTION_STATUS.md` — connection status indicator + connect/cancel/disconnect control.
- `Bearing - Editor.dc.html` — hi-fi interactive prototype of every state above.
- `Bearing - Logo.dc.html` — brand sheet (mark, lockups, sizes, palette).
- `support.js` — runtime needed to open the two prototypes locally; not part of the design.
