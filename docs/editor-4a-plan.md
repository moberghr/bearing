# Editor 4a redesign — implementation plan (handoff)

Written 2026-07-18. This is the executable plan for recreating the **Editor 4a** design
(`docs/design/editor-4a/README.md` + `Squirrel - Editor 4a.dc.html` prototype) in the Avalonia app.
The design bundle is high-fidelity: colors, spacing, and interaction states are final — match them.
It is a **design reference, not code to port** — recreate the look in Avalonia styles/`ControlTheme`/
`DynamicResource`, reusing existing controls (AvaloniaEdit editor, DataGrid results, schema tree).

The auto-memory `squirrel-project.md` has broader project state; `docs/result-set-plan.md` covers the
now-complete result-set work this builds on top of.

## How to work / verify

- Build app: `dotnet build src/Squirrel.Desktop/Squirrel.Desktop.csproj`
- Run app: `dotnet run --project src/Squirrel.Desktop`
- Tests (fish): `set -x SQUIRREL_TEST_PG_PORT 5434; dotnet test tests/Squirrel.<Proj>.Tests/...`
  (Bash tool is bash: `SQUIRREL_TEST_PG_PORT=5434 dotnet test ...`). Projects: `Sql`, `App`, `Data`,
  `Persistence`. Current totals: 44 Sql, 27 App, 10 Data, 11 Persistence (92 green).
- Test DB: docker `squirrel-pg-test` on **port 5434**, db `pagila`, user `postgres`, pw `squirrel`.
- **GUI can't be driven headlessly** (Wayland blocks synthetic input); `import`/xdotool screenshots DO
  work. The **user does live visual QA** after each phase — this plan's phases are sized to be
  independently eyeball-verifiable. Never claim visual states verified without the user.
- Rule: never block the UI thread on async (`.GetAwaiter().GetResult()`), especially on close.

## Decisions (locked with the user)

1. **Plan-first** — this doc, reviewed before any code.
2. **Real DB switching** — the toolbar Server and Database pills are independent. Server = connection;
   Database = a database *on that server*. Switching the Database pill opens a session against that DB
   reusing the connection's credentials. This is a **new backend capability** (today `ConnectionInfo`
   carries one fixed `Database`). Scoped into Phase 3.

## Behavior delta (what changes, end to end)

**Before**: FluentTheme default (light/system) with a blue accent. Layout is a `DockPanel` — a Project
bar and an Action bar stacked on top, a plain status bar at the bottom, and a `SplitView` whose single
left pane stacks the schema `TreeView` over a flat scripts `ListBox` (toggled by a `☰` button). History
is a **separate window** (`HistoryWindow`). Connection selection is a per-tab `ComboBox`; there is no
database selector, no menu bar, no focus mode. The per-connection `EnvironmentColor` shows only as small
dots (connection combo, schema badge, editor-tab dot).

**After**: A Kanagawa dark theme driven by a token resource dictionary. A single app-level
`ConnectionBrush` (following the **selected tab's** connection) recolors — together — the active editor
tab's top accent + name label, every connection dot, the results accent, and a **3px line across the
bottom status bar**, so the danger level of the target env reads at a glance. The shell becomes: title
bar → toolbar (proj/server/database pills + understated Run/Focus) → body [**52px icon rail** · **262px
swappable side panel** (Schema / Scripts / History) · editor column] → status bar. Scripts gain
**folders**; History becomes an **inline panel** (day-grouped, filter pills). Server/database selection
moves to **pill + Popup dropdowns** with real DB switching. An **Alt menu bar + File flyout** appears
(Open/Save live only there). A **focus mode** overlay gives distraction-free editing.

**Risk surface**: (a) theme swap touches every control — FluentTheme dark base + DataGrid + AvaloniaEdit
themes must all read correctly on the dark palette; (b) DB switching adds a real connection/session path
(credentials reuse, session lifecycle, schema snapshot per DB) — the only phase with money/data-adjacent
risk; (c) the rail/panel restructure retires the `☰` SplitView and the standalone `HistoryWindow`, so
session-persistence of side-pane state and history wiring must be carried over.

## Current state → gap analysis

| Area | Today | Handoff wants | Gap |
|---|---|---|---|
| Theme | FluentTheme default variant, blue accent | Kanagawa dark, token palette | **Full** — new theme + token dict |
| Connection color | per-connection dots only | app-wide `ConnectionBrush` (tab accent, dots, results, status line) | **Large** — central plumbing missing |
| Title bar | OS default chrome | 36px custom bar (dots, centered title, ⌥ hint) | Medium (or keep OS chrome — see open Q) |
| Toolbar | 2 stacked bars, ComboBoxes, labeled Run/Open/Save | 1 bar: proj/server/db **pills** + Popup dropdowns, 30×30 Run + Focus | **Large** |
| Left rail | none (`☰` toggles pane) | 52px icon rail (Connections/Schema/Scripts/History/Settings) | **New** |
| Side panel | one pane stacks schema+scripts | 262px, **swaps** by rail selection | **Large** — new `ActivePanel` nav |
| Schema panel | TreeView (server→db→objects→cols) exists | same tree, restyled; PK/FK badges, DEFAULT badge, Indexes node | Small-medium (mostly restyle) |
| Scripts panel | flat `ListBox` | **folder tree** (folder→scripts, counts, unsaved dot) | **Medium** — folder model |
| History | separate `HistoryWindow` | **inline panel**, day-grouped, filter pills, conn dots | **Medium** — move + regroup |
| Editor tabs | restyled TabStrip, blue underline | 2px **top** border in `ConnectionBrush`, conn name label in conn color | Small (recolor + top border) |
| Editor body | AvaloniaEdit, default highlight | Kanagawa syntax colors, current-line highlight, orange line no. | Medium (highlight theme) |
| Results dock | `ResultView`, Stacked/Tabbed exists | restyle header/toolbar/grid to tokens, orange segmented toggle | Small-medium (restyle) |
| Menu bar | none | Alt menu (File/Edit/View/Query/Help) + File flyout | **New** |
| Focus mode | none | full-window overlay, centered 820px editor, bottom conn line | **New** |
| DB switching | one DB per connection | server + database independent, switch DB on server | **New backend capability** |

## Architecture decisions (recommended)

- **Theme = FluentTheme (Dark) base + token `ResourceDictionary` + targeted overrides.** Keep
  `FluentTheme` for stock controls but force `RequestedThemeVariant="Dark"` and override the Fluent
  system brushes we rely on. Add a `Themes/Tokens.axaml` `ResourceDictionary` with every design token as
  a named `SolidColorBrush` (`Bg.Window`, `Bg.Chrome`, `Bg.Editor`, `Text.Primary`, `Accent.Orange`,
  `Syntax.Keyword`, …). Bespoke widgets (rail tiles, toolbar pills, dropdown rows) get their own
  `ControlTheme`/`Style` in `Themes/Controls.axaml`. Do **not** hand-roll a full control library.
- **`ConnectionBrush` is the linchpin — an app-level `DynamicResource`.** One `SolidColorBrush` resource
  updated in place when the selected tab's connection changes; every accent (`{DynamicResource
  ConnectionBrush}`) recolors automatically. Update site: `MainWindowViewModel` already reacts to
  `SelectedTab`/its connection (`tab.ConnectionColor`); add a hook that writes
  `Application.Current.Resources["ConnectionBrush"]`'s color from the active connection's env color.
  Keep the per-tab dot bindings; they can also point at `ConnectionBrush` where "active" is meant.
- **Align env colors to the handoff palette**: production `#E46876`, staging `#E6C384`, local `#7AA89F`
  (today's demo seed uses `#3FB950` for local). Cosmetic; update the seed + any presets.
- **Side-panel navigation via `ActivePanel` enum** (`Schema | Scripts | History`) on
  `MainWindowViewModel`, plus keep `SidePaneOpen`. The rail is an `ItemsControl`/toggle-button column
  bound to `ActivePanel`; the 262px panel is a `ContentControl` (or stacked panels toggled by
  `IsVisible`) selecting the active panel view.
- **Dropdowns = `Popup`/`Flyout`** anchored to the pill; toggle a colored border on the pill while open.
  Track `OpenDropdown` (`None | Server | Database`) on the VM (design §6).
- **Icons = vector `PathIcon`/`Path`, not font glyphs.** The app font clips symbol glyphs (learned in the
  result-set work: `↗`/`✕` are drawn as `Path`). Build a small `PathIcon` resource set (server, database,
  table, folder, run ▶, focus ⤢, disclosure ▸/▾) rather than Unicode in `TextBlock`s.

---

## Phase 1 — Theme foundation: tokens + `ConnectionBrush` + dark restyle  (DONE 2026-07-18, awaiting user live QA)

**Goal**: the existing UI, recolored to Kanagawa, with `ConnectionBrush` threaded. No layout change yet —
easy to eyeball against the prototype's colors, low structural risk.

**Shipped** (builds; 40 App tests green; app launches clean — not yet eyeball-QA'd, Wayland blocks my
screenshots so the user verifies the visuals):
- `src/Squirrel.App/Themes/Tokens.axaml` — `ResourceDictionary` of every design token as a named brush
  (`Bg.Window`/`Bg.Chrome`/`Bg.Editor`/`Bg.LineActive`/`Bg.TileActive`/`Bg.Select`/`Bg.Hover`,
  `Border`/`Border.Control`, `Text.*`, `Accent.Orange`, `Syntax.*`, `Ok.Green`, `Error.Red`) + geometry
  `x:Double`s (radii, region heights, rail/panel widths).
- `App.axaml` — `RequestedThemeVariant="Dark"`; merges `Tokens.axaml`; declares the mutable
  `ConnectionBrush` (`SolidColorBrush`, default neutral `#54546D`). `App.SetConnectionAccent(hex)`
  (App.axaml.cs) mutates that brush's `.Color` so every `{DynamicResource ConnectionBrush}` recolors at
  once.
- **Plumbing**: `MainWindowViewModel.ActiveConnectionColor` (= selected tab's `ConnectionColor`), notified
  from `OnSelectedTabChanged` + `SetTabConnection`. `MainWindow.axaml.cs` reacts (`OnViewModelPropertyChanged`
  → `App.SetConnectionAccent`) and seeds it in `HookViewModel`. `src/Squirrel.App/Theming/ConnectionColors.cs`
  (`Resolve(hex)` → `Color`, neutral fallback) — unit-tested (`ConnectionColorsTests`, 7 cases).
- `MainWindow.axaml` recolored to tokens: window/bars/side pane/tab strip surfaces; **status bar = 3px
  top border in `ConnectionBrush`**; **editor tabs = 2px *top* accent in `ConnectionBrush`** (was a blue
  bottom underline) + a **connection-name label in the connection color**; results sub-tab accent →
  `ConnectionBrush`; section labels → `Text.Dim`.
- Editor chrome (`InstallSqlHighlighting`): Kanagawa surface `Bg.Editor`, current-line `Bg.LineActive`
  (no contrasting border box), faint line numbers `Text.Faint`; selection brush → translucent wave-blue.
- Demo seed env color `#3FB950` → handoff local `#7AA89F`.

**Deviation (syntax hues deferred)**: exact Kanagawa keyword/func/number colors are **not** applied —
TextMateSharp 2.0.3 exposes no public API to load a custom theme (`ThemeReader` is internal), and the
handoff explicitly marks syntax highlighting as its one loose area (§Fidelity). Kept **DarkPlus** grammar
colors; the editor *chrome* is Kanagawa. A ready-made `docs/design/editor-4a/kanagawa.color-theme.json`
is parked for when a custom-theme path (custom `IRegistryOptions`/`IRawTheme`) is worth building. Also
deferred: the current line's line-number turning **orange** (AvaloniaEdit's line-number margin recolors
uniformly, not per-line) — line numbers are uniformly faint for now.

### Original task list (for reference)

- Add `src/Squirrel.App/Themes/Tokens.axaml` — `ResourceDictionary` of all design tokens (§Design tokens
  table) as named brushes + typography/geometry constants (radii, region heights as `x:Double`).
- `App.axaml`: `RequestedThemeVariant="Dark"`; merge `Tokens.axaml`; override the Fluent system brushes we
  depend on (window/panel background, control background/border, foreground) to token values; add the
  app-level mutable `ConnectionBrush` (`<SolidColorBrush x:Key="ConnectionBrush" Color="#7AA89F"/>`).
- Recolor `MainWindow.axaml` regions to tokens (project/action bars, status bar, side pane, tab strip).
- **Status bar**: add a 3px top border filled with `{DynamicResource ConnectionBrush}`.
- **Editor tabs**: selected tab gets a 2px **top** border in `ConnectionBrush` (currently a bottom
  underline in blue); add the connection-name label rendered in the connection color.
- **AvaloniaEdit**: apply a Kanagawa highlight theme (keywords `#957FB8`, functions `#7E9CD8`, numbers
  `#FFA066`); current-line highlight `#252535` with an orange line number. Reuse the existing highlight
  install path (`InstallSqlHighlighting` in `MainWindow.axaml.cs`).
- **`ConnectionBrush` plumbing**: in `MainWindowViewModel`, when `SelectedTab` or its connection changes,
  set the app `ConnectionBrush` color from the active connection's `EnvironmentColor` (fallback neutral).
  Add a tiny helper (testable): env color → `Color`. Wire the update in the view (App resource mutation
  is a UI concern — do it in code-behind reacting to a VM event/property).
- Update the demo seed env colors to the handoff palette.

**Verify**: build + run; user eyeballs palette, status-bar line recolors when switching a tab's
connection, tab top-accent + name label track the connection, syntax colors match.
**Tests**: unit-test the env-color→Color mapping (App). No behavior change to logic paths.

## Phase 2 — Shell: left icon rail + swappable side panel (+ inline History)

**Goal**: replace the `☰`-toggled stacked pane with the 52px rail + 262px swappable panel; bring History
inline. Biggest structural change; do it before the toolbar so panels have their home.

- **Left rail** (`52px`): a vertical column of 36×36 icon toggle tiles — Connections, Schema, Scripts,
  History, spacer, Settings. Idle transparent + `#727169` glyph; hover `#20202A`; active tile
  `Bg.TileActive` + orange glyph (full rounded tile, **no** left-edge indicator). Bound to `ActivePanel`.
- **`ActivePanel` enum** on the VM + a `ContentControl` (or three `IsVisible`-toggled panels) in the
  262px column. Keep `SidePaneOpen`/width persistence.
- **Schema panel**: reuse the existing `ServerNodes` `TreeView`; restyle to the monospace tree spec
  (line-height, disclosure triangles `#54546D`, connection-color dot, `DEFAULT` badge, PK orange / FK
  `#957FB8` column badges, `▸ Indexes (n)` node). Header `SCHEMA` + search glyph.
- **History panel**: move `HistoryWindow`'s query-log search into a `HistoryPanelViewModel` (reuse
  `SearchHistoryAsync`). Group rows by day (`TODAY`/`YESTERDAY`/date), filter pills (`All`/`✓ ok`/
  `✕ error`), each row = conn-color dot + truncated monospace query + right time; selected row
  `Bg.Select` + 2px orange left border; error rows in `Error.Red` prefixed `✕`. Keep `HistoryWindow` as
  a fallback or retire it (open Q).
- **Scripts panel**: keep flat for this phase (folders in Phase 4) but restyle header (`SCRIPTS` +
  new-folder + new-script) and filter input.
- **Settings** tile: route to existing settings or stub with a tooltip.

**Verify**: rail switches panels; history shows inline, grouped, filterable; schema tree restyled.
**Tests**: `HistoryPanelViewModel` grouping/filter (App), `ActivePanel` switching (App).

## Phase 3 — Toolbar: proj/server/database pills + dropdowns + real DB switching

**Goal**: the single 46px toolbar with pill selectors and Popup dropdowns, **and** the DB-switching
backend. Highest-risk phase (touches connection/session lifecycle).

- **Backend — DB switching**:
  - Metadata: add "list databases on server" to `IMetadataReader`/`PostgresMetadataReader`
    (`SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY 1`).
  - Session: allow opening a session against a chosen DB on an existing connection, reusing credentials —
    likely a derived `ConnectionInfo` with a different `Database` (same `Id` family or a keyed variant) so
    `ConnectionSessionManager`/`SchemaBrowser` (already per-`(conn, db)`) resolve it. Decide how the
    active `(server, database)` pair is modeled on the tab (today `SelectedTabConnection` is one
    `ConnectionInfo`). Recommend: tab holds `(ConnectionId, DatabaseName)`; the effective connection
    string swaps the DB. Confirm session cleanup on switch.
  - Schema snapshot + `ConnectionBrush` follow the active `(server, db)`.
- **Toolbar UI** (replaces both current bars):
  - **Proj** pill (`PROJ analytics ▾`) → project selector (reuse `RecentProjects`).
  - **Server** pill (server icon + host + `▾`); opens the server dropdown (280px: `CONNECTIONS` caption,
    per-connection env dot + host + `env · postgres <ver>` subtitle, active `✓`, `＋ New connection…`).
    Border turns `#7E9CD8` while open.
  - `›` separator, **Database** pill (db icon + name + conn-color dot + `▾`); opens the database dropdown
    (250px: search, DB rows, active `DEFAULT` + `✓`). Border `#98BB6C` while open.
  - **Run** 30×30 icon-only (green ▶, `border.control`, tooltip `Run ⌘⏎`); **Focus** 30×30 (⤢).
  - `OpenDropdown` state; backdrop click-catcher closes; opening a dropdown closes the Alt menu.
- Retire the old Project bar + Action bar `ComboBox`es.

**Verify (with DB present)**: switch server → schema/brush update; switch database → new DB's schema,
queries run against it; dropdowns open/close, pill borders color.
**Tests**: list-databases (Data, live pagila), DB-switch session resolution (App), `OpenDropdown` logic.

## Phase 4 — Scripts folders

**Goal**: the folder tree for scripts (design §3).

- Extend the scripts model: folders (name, count, collapsed) → scripts (name, unsaved flag), plus
  ungrouped scripts. Back it with the on-disk `ScriptsDirectory` layout (subfolders = folders). Update
  `JsonProjectStore`/scripts enumeration to read one level of subdirectories.
- Folder tree UI: `🗀` amber folders w/ right-aligned counts (`text.faint`), file glyph `#727169`
  (orange when active), selected `Bg.Select` + orange unsaved dot. New-folder / new-script actions +
  filter input.

**Verify**: folders render, counts correct, new/rename/open work, unsaved dot shows.
**Tests**: scripts folder enumeration/grouping (App/Persistence).

## Phase 5 — Alt menu bar + File flyout

**Goal**: design §, interaction rules in §Interactions.

- Alt toggles a menu bar (`File/Edit/View/Query/Help`) hidden by default + a File flyout (New Query ⌘N,
  Open… ⌘O, Open Recent ▸, Save ⌘S, Save As… ⇧⌘S, Close Tab ⌘W). **Open/Save live only here** (remove
  from toolbar — already gone after Phase 3). `IsMenuVisible` state; Esc closes it (and any dropdown).
- Wire menu items to existing commands (New/Open/Save/Close already exist as handlers).

**Verify**: Alt shows/hides menu; File actions work; Esc closes.
**Tests**: `IsMenuVisible` toggle + Esc-closes logic (App).

## Phase 6 — Focus mode

**Goal**: distraction-free overlay (design §7).

- `IsFocusMode` state; full-window overlay (`Bg.Editor`, above all) with a slim 34px top bar
  (filename + unsaved • + conn dot/name; small ▶ Run + `⤡ Exit` + `Esc` hint), a centered editor column
  (max-width 820px, 15px/1.85, same syntax + current-line highlight), and a 3px `ConnectionBrush` line
  pinned to the bottom. No rail/panel/results.
- Focus button / `Ctrl+⌘F` enters; `Esc` exits (and closes menus/dropdowns).
- Share the AvaloniaEdit document with the main editor (same tab text) so edits are consistent.

**Verify**: enter/exit focus, editing persists, bottom line colors, Esc exits.
**Tests**: `IsFocusMode` toggle logic (App).

---

## Open questions (resolve before the relevant phase)

1. **Custom title bar?** Handoff shows a 36px bar with macOS traffic-lights, centered title, ⌥ hint.
   Recommend **keeping OS window chrome** (the handoff calls the dots decorative) and only theming it, or
   an `ExtendClientAreaToDecorationsHint` custom bar if you want the exact look. — *Phase 1/2.*
2. **Retire `HistoryWindow`?** Once History is an inline panel (Phase 2), keep the window as a "pop out"
   or delete it. Recommend delete to avoid two code paths.
3. **DB-switch tab model** (Phase 3): does a tab bind `(ConnectionId, DatabaseName)` or a derived
   `ConnectionInfo`? Affects persistence (`JsonSessionStore`) and `SelectedTabConnection`'s type.
4. **Settings panel** scope — is there an existing settings surface to route the rail's ⚙ to, or is it a
   stub for now?

## Key files (where each phase lands)

- `src/Squirrel.App/App.axaml` — theme variant, merged token dict, `ConnectionBrush` resource *(P1)*.
- `src/Squirrel.App/Themes/Tokens.axaml`, `Themes/Controls.axaml` — **new**: token brushes + bespoke
  control themes (rail tiles, pills, dropdown rows, badges) *(P1–P3)*.
- `src/Squirrel.App/Views/MainWindow.axaml{,.cs}` — layout (rail + panels + toolbar), `ConnectionBrush`
  update, dropdown Popups, Alt menu, focus overlay *(all phases)*.
- `src/Squirrel.App/ViewModels/MainWindowViewModel.cs` — `ActivePanel`, `OpenDropdown`, `IsMenuVisible`,
  `IsFocusMode`, active `(server, db)` model, DB-switch methods *(P2–P6)*.
- `src/Squirrel.App/ViewModels/SchemaNodes.cs` — tree node glyphs/badges restyle *(P2)*.
- `src/Squirrel.App/ViewModels/ScriptItem.cs` + a new `ScriptFolder` model — folders *(P4)*.
- New `HistoryPanelViewModel` (from `Views/HistoryWindow.axaml.cs`) — inline history *(P2)*.
- `src/Squirrel.App/Controls/ResultView.cs` — results header/toolbar/grid restyle to tokens *(P1)*.
- `src/Squirrel.Core/Data/IDbProvider.cs` (IMetadataReader) + `Squirrel.Data/Postgres/
  PostgresMetadataReader.cs` — list-databases *(P3)*.
- `src/Squirrel.App/Connections/ConnectionSessionManager.cs`, `SchemaBrowser.cs` — DB-switch session
  resolution (already per-`(conn, db)`) *(P3)*.
- `src/Squirrel.Persistence/JsonSessionStore.cs`, `JsonProjectStore.cs` — persist active DB + script
  folders *(P3–P4)*.
- Reference: `docs/design/editor-4a/README.md` (spec) + `Squirrel - Editor 4a.dc.html` (prototype).

## Suggested order & why

P1 (theme) first — highest visible value, foundational (`ConnectionBrush` + tokens everything else
binds to), lowest risk. P2 (shell) next — everything later needs the rail/panel home. P3 (toolbar + DB
switching) — the one functional/risky phase; do it once the shell exists. P4–P6 (scripts folders, menu,
focus) are additive polish, independently shippable in any order. Each phase leaves the app building,
tested, and eyeball-verifiable by the user.
