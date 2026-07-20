# Keybindings overhaul — implementation plan

Written 2026-07-19. Goal: replace the three hand-rolled key dispatchers with **one unified
keybinding system**, make every binding **user-configurable**, and add a **command palette** that
doubles as the discoverability surface. The auto-memory has the broader project state; this file is
the executable plan.

## How to work / verify

- Build app: `dotnet build src/Squirrel.Desktop/Squirrel.Desktop.csproj`
- Run app: `dotnet run --project src/Squirrel.Desktop`
- Tests (fish shell): `set -x SQUIRREL_TEST_PG_PORT 5434; dotnet test tests/Squirrel.<Proj>.Tests/Squirrel.<Proj>.Tests.csproj`
  - Projects: `Sql`, `App`, `Data`, `Persistence`.
- **Bash tool runs bash, not fish** — use `SQUIRREL_TEST_PG_PORT=5434 dotnet test ...` there.
- GUI can't be driven headlessly (Wayland blocks synthetic input); key *resolution* is unit-testable
  without a UI — that's the whole point of the design below. User does live QA of actual keystrokes.

## Why (the problem being solved)

Today key handling lives in **three unrelated places**, each with its own ad-hoc matching:

1. `MainWindow.OnKeyDown` — a long `if/else` chain of `e.Key == … && ctrl` (app-global: Run, Save, tabs…).
2. `MainWindow.OnEditorKeyDown` — a `switch` in the tunnel phase (editor: fold, comment, open-line…),
   with **layout-aware `PhysicalKey`** matching for brackets/comment (works on the Croatian layout).
3. `ResultView.OnGridKey` — another `switch` in the tunnel phase (grid: copy, nav, edit, delete…).

Plus menu `InputGesture` strings in `MainWindow.axaml` that are **display-only** and have already
**drifted** from reality:

- `Ctrl+N` "New Query" — advertised, **no handler** (the real new-tab is `Ctrl+T`).
- `Ctrl+Shift+S` "Save As…" — advertised, **dead**.

Consequences: no configurability, near-zero discoverability (menu is Alt-tap-hidden and lists ~8 of
~30 shortcuts), no single source of truth, and no keyboard path for whole flows (tab switching,
region focus, FK jump, back-nav, connection/db switch).

## Decisions (to confirm with user before Phase 1)

1. **One command registry + one dispatcher.** Every keyboard-triggerable action is a `Command` with a
   stable string id. A single resolver maps a keystroke (in the current scope) to a command id and
   invokes it. The three dispatchers collapse into thin per-control adapters that all call the same
   resolver.
2. **Scopes, not one flat map.** A keystroke resolves against the **focused scope** first, then falls
   back to `Global`. Scopes: `Global`, `Editor`, `Grid`, `Tree`, `Palette`. This preserves today's
   "editor/grid handle it in tunnel phase, app-level in bubble" behavior declaratively.
3. **Gestures carry logical OR physical keys.** The config format must express both `Ctrl+/` (logical)
   and `Ctrl+Shift+PhysBracketLeft` (physical) — otherwise we lose the Croatian-layout handling. This
   is the non-obvious constraint; naive `"Ctrl+Shift+BracketLeft"` won't reproduce today's behavior.
4. **Config layered over defaults.** Built-in defaults live in code (a `KeymapDefaults` table). A user
   `keybindings.json` in `SquirrelPaths.ConfigDir` overrides/adds/removes on top. Missing file =
   defaults only. This mirrors the existing JSON-store pattern (`JsonSessionStore`, atomic tmp+move).
5. **The menu is generated from the keymap.** `InputGesture` text is looked up from the active keymap,
   never hardcoded — kills the drift bug class permanently.
6. **Command palette is the discoverability surface.** `Ctrl+Shift+P` (and/or `Ctrl+P`) opens a
   fuzzy-searchable list of every command with its current binding shown. Reuse the tree's existing
   fuzzy-find + match-highlight logic.
7. **AvaloniaEdit built-ins stay as-is** (undo/redo/word-nav/etc.) — not ours to own; the registry
   only covers commands we currently hand-roll. Document this boundary; revisit later if users ask.

## Target architecture

New project area: `src/Squirrel.App/Input/` (pure-ish, unit-testable), plus a small persistence type.

```
Input/
  Command.cs            // { Id, Title, Scope, Group, Func<CommandContext,bool> Run, bool CanRun }
  CommandRegistry.cs    // id -> Command; enumerable for the palette; grouped by Scope/Group
  Gesture.cs            // normalized keystroke: modifiers + (LogicalKey? | PhysicalKey?)
  GestureParser.cs      // "Ctrl+Shift+PhysBracketLeft" <-> Gesture  (round-trips for JSON + display)
  Keymap.cs             // (Scope, Gesture) -> commandId ; layered defaults + user overrides
  KeymapDefaults.cs     // the built-in table (everything in "Current shortcuts" below)
  KeyDispatcher.cs      // Resolve(KeyEventArgs, Scope) -> commandId?; the ONE matcher
  CommandContext.cs     // handle passed to Run(): Vm, Editor, active grid/result, etc.
```

Persistence: `src/Squirrel.Persistence/JsonKeymapStore.cs` → `<ConfigDir>/keybindings.json`
(global, not per-project — bindings are a user preference, like an editor config).

### Dispatch flow (replaces all three today)

- `MainWindow` and `ResultView` keep their `AddHandler(KeyDownEvent, …, Tunnel)` registrations, but the
  handler body becomes: `var scope = ScopeFor(sender/focus); if (dispatcher.TryResolve(e, scope) is {} id
  && registry.Run(id, ctx)) e.Handled = true;`
- The **tunnel-vs-bubble** split is expressed by which scopes a given control's tunnel handler asks for
  (Editor/Grid resolve their scope in tunnel so they win over the built-in control; Global resolves at
  the window bubble handler as today).
- `Escape`'s cascade (overlay → menu → cancel) becomes an ordered set of commands each with a `CanRun`
  guard, tried in priority order — or stays as one `escape.contextual` command that runs the cascade
  internally. Keep it one command; the cascade is intrinsic, not user-orderable.

### Gesture model detail

```csharp
readonly record struct Gesture(
    KeyModifiers Modifiers,
    Key? Logical,          // e.g. Key.OemQuestion for Ctrl+/
    PhysicalKey? Physical);// e.g. PhysicalKey.BracketLeft for layout-independent fold
```

Matching precedence in `TryResolve`: a Physical-based binding matches on `e.PhysicalKey`; a
Logical-based binding matches on `e.Key`. When both a physical and logical binding could apply to one
scope, physical wins (that's today's behavior for brackets/comment). Serialize physical keys with a
`Phys` prefix (`PhysBracketLeft`) so JSON and the palette can show the distinction.

## Current shortcuts (the default keymap to encode)

These become `KeymapDefaults`. Scope in brackets. `*` = new (see gaps) — not part of "current", listed
so Phase 3 has a target.

**Global:** `F5`/`Ctrl+Enter` Run · `Ctrl+Space` Complete · `Ctrl+S` Save · `Ctrl+O` Open ·
`Ctrl+T` New tab · `Ctrl+W` Close tab · `Ctrl+B` Toggle side pane · `Ctrl+R` Toggle results ·
`F2` Rename tab · `Alt+Up`/`Alt+Down` Prev/next statement · `Alt`(tap) Toggle menu · `Escape` cascade.

**Editor:** `Shift+Enter`/`Ctrl+Shift+Enter` Open line below/above · `Ctrl+/`(+`Ctrl+-` HR) Toggle
comment · `Ctrl+Shift+A` Select statement · `Ctrl+Shift+[`/`]` Fold/unfold current (physical) ·
`Ctrl+Shift+-`/`Ctrl+Shift+=` Fold/unfold all.

**Grid:** `Ctrl+C`/`Ctrl+Insert` Copy · `Ctrl+A` Select all · `Delete` Delete rows · `Enter`/`F2`
Begin edit · `Escape` Clear selection · arrows/Home/End/PageUp/Down (+`Ctrl` edges, +`Shift` extend).

**Tree:** type-ahead find · `Esc` clear · `Backspace` del char · `Up`/`Down` next/prev match ·
`Enter` open (scripts).

**Dialogs:** `Enter` accept · `Esc` cancel (TextPromptDialog only today).

## Phased plan

### Phase 1 — Unify onto the registry (no behavior change, no config yet)  — DONE 2026-07-20
Build `Command`, `CommandRegistry`, `Gesture`, `GestureParser`, `Keymap`, `KeymapDefaults`,
`KeyDispatcher`, `CommandContext`. Port **all** current shortcuts into `KeymapDefaults`. Rewrite the
three handlers to delegate to the dispatcher. Generate menu `InputGesture` text from the keymap.
**Outcome:** identical behavior, one code path, and the two dead menu entries either work or are
removed. Heavily unit-tested: parser round-trips, physical-vs-logical precedence, scope fallback,
Escape cascade ordering. This is the load-bearing phase; do it cleanly before anything else.

**What shipped** (`src/Squirrel.App/Input/`): `KeyScope`, `Gesture`, `GestureParser`, `KeyCommand`,
`CommandRegistry`, `Keymap` (+ `KeyBinding`), `KeymapDefaults`, `KeyDispatcher`, `CommandIds`. The
three dispatchers now delegate: `MainWindow.OnKeyDown` → `TryHandle(e, Global)`, `OnEditorKeyDown` →
`TryHandle(e, Editor)`, `ResultView.OnGridKey` → `TryHandle(e, Grid)`. Global+Editor commands register
in `MainWindow.RegisterCommands`; Grid commands in `ResultView.RegisterGridCommands` (into the shared
registry, via `ResultsView.CommandDispatcher`). Menu gestures set in `MainWindow.SyncMenuGestures` from
`Keymap.DisplayGesture`. The dead `Ctrl+N` / `Ctrl+Shift+S` are now real (bound to `tab.new` /
`file.saveAs`). 20 new tests in `tests/Squirrel.App.Tests/KeybindingTests.cs` (103 App green, 70 Sql,
Desktop builds, app launches clean). **Awaiting user live QA** (Wayland blocks synthetic-input tests).

**Design notes / deviations from the sketch above:**
- **Scope fallback is achieved by event bubbling, not by the resolver.** Each control's tunnel handler
  resolves ONLY its own scope; unclaimed keys bubble to the window, which resolves `Global`. This
  matches the old tunnel(editor/grid)+bubble(window) split exactly, so no cross-scope fallback logic
  was needed. `KeyScope.Tree`/`Palette` are declared but unused until later phases.
- **Grid spatial navigation stayed local** (arrows/Home/End/PageUp-Down, Shift-extend, Ctrl-edges).
  It's cell-cursor *motion*, not a rebindable command — the same line we already draw around
  AvaloniaEdit's caret motion. Only the grid's discrete commands (copy/select-all/delete/begin-edit/
  clear) go through the registry. `OnGridKey` still exists but its head now just does
  `TryHandle(e, Grid)` then falls to nav.
- **`CommandContext` wasn't needed.** Command delegates close over `MainWindow`/`ResultView`, so they
  reach `Editor`/`Vm`/`_folding` directly. Grid commands read a transient `_keyTarget` (grid+result)
  set at the top of `OnGridKey` so they act on the grid that received the key.
- **`CanRun` replaces the old contextual guards.** Escape only claims the key when there's something
  to dismiss (overlay/menu/busy); Grid delete/begin-edit only when the set is editable; clear-selection
  only when a selection exists. When `CanRun` is false the dispatcher leaves the key unhandled so it
  falls through / bubbles — reproducing the old behavior precisely.
- **Meta folds to Control** in `Gesture` normalization (macOS Cmd ≡ Ctrl; also preserves the grid's
  old `ctrl || meta` copy behavior).
- **`Key.Enter` stringifies as `Return`** (shared enum value) — `GestureParser` has an `Enter` alias so
  config text and the menu show the friendly form.

### Phase 2 — Configurability  — DONE 2026-07-20
`JsonKeymapStore` reads `<ConfigDir>/keybindings.json` and layers over defaults (add / rebind /
`"unbind"`). Conflict detection **within a scope** (two commands, same gesture → last-wins + a
surfaced warning in status bar). Round-trip: unknown command ids and unparseable gestures are skipped
with a warning, never crash. Tests in `Squirrel.Persistence.Tests` + `Squirrel.App.Tests`.

**What shipped** (`src/Squirrel.App/Input/`): `KeymapConfig.cs` (`KeyBindingEntry` DTO +
`KeymapLoadResult`) and `KeymapLoader.cs`. `keybindings.json` is a **top-level JSON array** of
`{ key, command, scope? }` (VS Code style), layered over `KeymapDefaults` at startup in
`MainWindow` (`KeymapLoader.LoadFromConfig`), feeding the dispatcher. Warnings surface once in the
status bar via `HookViewModel`. 12 new tests (`KeymapLoaderTests.cs`; 115 App green, 11 Persistence,
full solution builds, app launches clean with a valid+invalid test config). **Awaiting user live QA.**

**Format / behavior:**
- `{ "key": "F8", "command": "run" }` — add a gesture (scope **inferred** from the command's default
  binding, so scope is usually omitted). `"scope": "Grid"` overrides inference / is required for a
  command that has no default binding.
- `{ "key": "F5", "command": "-run" }` — unbind one gesture (`-` prefix).
- `{ "command": "-editor.foldAll" }` — **keyless unbind** drops all of a command's gestures.
- Rebind = unbind + bind. Binding a taken gesture **displaces** the old command (one command per
  (scope, gesture)) and warns.
- **Deviation from the sketch:** the loader lives in `Squirrel.App/Input/`, not
  `Squirrel.Persistence` — `SquirrelJson.Options` is `internal` and the config is about App-layer
  command ids / `Gesture` / `KeyScope`, so it belongs with them. It still writes under
  `SquirrelPaths.ConfigDir`. Tests are therefore all in `Squirrel.App.Tests`. `Apply` and
  `LoadFromJson` are pure (no file IO) → fully unit-tested; only the thin `File.ReadAllText` in
  `LoadFromConfig` isn't.
- Everything is best-effort: unreadable file / malformed JSON / unknown command / unparseable gesture
  → skipped with a status-bar warning, defaults still apply. Never throws.
- **Not yet built:** no auto-written template/example file (absence = defaults); no hot-reload (config
  read once at startup); no settings UI (that's Phase 4).

### Phase 3 — Command palette + fill the flow gaps  — DONE 2026-07-20
- Palette overlay (`Ctrl+Shift+P`): fuzzy list of every registered command, grouped, each row showing
  its current gesture; Enter runs it. Reuse tree fuzzy-find + `MatchHighlightConverter`. New `Palette`
  scope (`Up`/`Down`/`Enter`/`Esc`).
- Add the missing commands (all now trivial — just registry entries + defaults):
  **tab switching** (`Ctrl+Tab`/`Ctrl+Shift+Tab`, `Ctrl+PageUp/Down`, `Ctrl+1..9`),
  **region focus** (`F6` cycle editor↔results↔sidebar; `Ctrl+1/2/3` direct),
  **FK jump** from active cell, **back-nav** (`Alt+Left`),
  **panel select** (Connections/Scripts/History),
  **connection/db switch** + open connection dialog,
  **run variants** (run-all, run-and-advance),
  **Save As** (`Ctrl+Shift+S` — finally real), **New Query** consistency (`Ctrl+N` = `Ctrl+T`).
- Fix `ConnectionDialog`: `IsCancel` on Cancel so `Esc` closes it.

**What shipped:**
- **Command palette** — `Input/PaletteFilter.cs` (pure fuzzy ranking: subsequence match on title,
  contiguous-run + word-boundary bonuses, earlier-first; empty query = grouped/alphabetical) +
  an overlay built in `MainWindow` (`ShowPalette`/`HidePalette`/`RefreshPaletteList`/`OnPaletteKeyDown`,
  reusing the `OverlayLayer` + dim-backdrop pattern from the pending-script panel). Search box + ranked
  `ListBox` showing **title · group · current gesture**; `Up`/`Down`/`Enter`/`Esc`, double-click runs.
  Lists `_commands.All.Where(CanRun())`. While open it owns the keyboard — `OnKeyDown` short-circuits so
  no global shortcut fires underneath, and `HandleEscape` closes it first.
- **New commands + defaults:** `palette.open` (`Ctrl+Shift+P`); `tab.next`/`tab.prev`
  (`Ctrl+Tab`/`Ctrl+Shift+Tab`, `Ctrl+PageDown`/`Ctrl+PageUp`, wrap-around); `focus.cycle` (`F6`,
  editor→results→sidebar, skipping hidden regions — `ResultView.FocusableGrid` exposes the grid target);
  `grid.followFk` (`Alt+Right`, drills the active FK cell) + `grid.back` (`Alt+Left`, `CanGoBack`-gated);
  `panel.connections`/`panel.scripts`/`panel.history`, `connection.new`, `query.runAll` — all
  **palette-only** (unbound by default, bind in `keybindings.json`).
- **ConnectionDialog** Cancel is now `IsCancel="True"` → `Esc` closes it.
- 11 new tests (`PaletteFilterTests.cs`: ranking + the Phase-3 default bindings). 123 App green, app
  launches clean. **Awaiting user live QA** (palette focus/overlay + keystrokes need eyeballs on Wayland).

**Deviations / deferrals (from the sketch above):**
- **Palette self-handles its keys** rather than routing through a `Palette` `KeyScope` — the overlay's
  Up/Down/Enter/Esc are modal navigation (like the tree type-ahead / grid cell motion), so `KeyScope.Palette`
  stays declared-but-unused. Match highlighting inside rows was **not** wired (the reused
  `MatchHighlightConverter` idea) — rows show plain title + gesture; can add later.
- **`Ctrl+1..9` (go-to-tab-N) deferred** — would be 9 noisy palette entries and it collides with the
  sketch's `Ctrl+1/2/3` region-focus idea. Chose `F6` cycle for regions + `Ctrl+Tab` cycling for tabs.
  Direct-region `Ctrl+1/2/3` also deferred.
- **DB/server keyboard switching deferred** (needs combo/popup interaction); only *open connection
  dialog* (`connection.new`) shipped. **Run-and-advance deferred**; only `query.runAll` shipped.
- Save As (`Ctrl+Shift+S`) and New Query (`Ctrl+N`) were already made real in Phase 1.

### Phase 4 (optional, later) — Settings UI  — DONE 2026-07-20
A keybindings pane listing commands by scope with inline rebind capture ("press a key…"), reset, and
live conflict highlighting. Writes through `JsonKeymapStore`. The palette already covers 90% of daily
need, so this is genuinely optional.

**What shipped:**
- **`KeymapDiff.ComputeOverrides(defaults, edited)`** (`Input/KeymapDiff.cs`) — the correctness core.
  Diffs the edited keymap against defaults into the **minimal** `KeyBindingEntry` list (unbinds for
  removed defaults, binds for additions), ordered unbinds-first so a reassigned gesture applies without
  a displacement warning. Scope is emitted only for commands that ship unbound (nothing to infer from).
  Round-trip guaranteed: `Apply(defaults, ComputeOverrides(defaults, edited)) == edited`, tested.
- **`KeymapLoader.SaveOverrides`** — atomic write of the entry list to `keybindings.json`; an empty
  diff **deletes** the file (back to pure defaults).
- **`KeymapLoader.Apply/LoadFromConfig/LoadFromJson` gained a `knownCommands` param** — the registry's
  ids, so config/settings can bind **palette-only** commands (which have no default to infer from and
  must carry an explicit scope). `MainWindow` passes `_commands.All` ids at load.
- **`KeyDispatcher.Keymap` is now settable** → the edited map applies live (the grid shares the same
  dispatcher, so it updates too); `SyncMenuGestures` refreshes the menu.
- **`KeybindingsWindow`** (`Views/KeybindingsWindow.cs`, code-built like `ResultView`): commands grouped
  by scope; each shows its gestures as removable chips + an **"+ Add"** that captures the next keystroke
  ("press keys…", Esc cancels, tunnel handler so a captured Enter isn't eaten by the Save button);
  adding a taken gesture **displaces** the old and notes the reassignment; **Reset all**; Save/Cancel.
  Opened via the `settings.keybindings` command (palette) and **Edit ▸ Keyboard Shortcuts…**.
- 6 new tests (`KeymapDiffTests.cs`); 128 App green, Desktop builds, app launches clean. **Awaiting
  user live QA** (the window's capture/chips/live-apply can't be driven headlessly).

**Deviations / limits:**
- **Capture is logical-key only.** Physical bindings (the layout-independent fold keys) can't be
  captured in the UI — edit `keybindings.json` for those. The UI shows/removes existing physical chips
  fine (formatted as `Ctrl+Shift+PhysBracketLeft`).
- **No live cross-row conflict highlighting** beyond displace-on-add (the invariant is one command per
  (scope, gesture), so a conflict can't persist — the reassignment is surfaced as a status note instead).
- Changes apply on **Save** (no per-edit live preview); no per-command "reset to default" (only reset-all).

## Post-QA follow-ups (2026-07-20, after user live QA of Phases 1–4)

- **Settings-window chip fix**: gesture text used a non-existent brush key (`Text` → transparent) so
  chips looked empty; the `✕` remove glyph clips in the app font. Fixed to `Text.Primary` + a drawn
  vector ✕ (matching how `ResultView` draws its icons).
- **Menu now docks above the toolbar** (it was below): the `<Menu>` is the first `Dock="Top"` child.
- **Tab switching split into MRU vs visual + go-to-N** (was a single visual next/prev on Ctrl+Tab):
  - `tab.mruNext`/`tab.mruPrev` = **Ctrl+Tab** / **Ctrl+Shift+Tab**, most-recently-used order with the
    Alt-Tab feel — cycles while Ctrl is held, commits the landed tab as most-recent on Ctrl release
    (`OnKeyUp`). Backed by a testable `Input/MruList.cs`; cycle state (`_mruCycling`/`_mruIndex`) in the view.
  - `tab.next`/`tab.prev` = **Ctrl+PageDown** / **Ctrl+PageUp**, visual (tab-strip) order.
  - `tab.goto1..9` = **Ctrl+1..9** (9 = last tab, browser convention).
- **Direct focus**: `focus.editor` = **Ctrl+0**, `focus.results` = **Ctrl+Shift+0** (plus the existing
  `focus.cycle` = F6).
- **Toolbar pickers**: `select.project` = **Ctrl+Shift+J**, `select.connection` = **Ctrl+Shift+C**,
  `select.database` = **Ctrl+Shift+D** — focus the pill and drop its list open. (Gesture choices are
  defaults; all rebindable in the settings UI. Project got J because P/O/S collide with palette/open/save.)
- **Nav keys resolved in a window tunnel handler** (`OnWindowNavKey`) via a new
  `KeyDispatcher.TryHandle(e, scope, only)` filter, so tab/focus/picker gestures win over the framework's
  tab traversal and the editor/grid — without disturbing the tunnel(Editor/Grid)+bubble(Global) model for
  every other key. The `only` set (`_navCommands`) is exactly these commands, which don't overlap any
  Editor/Grid binding, so preempting them in tunnel is safe.
- Tests: `MruListTests.cs` + binding-resolution tests; 136 App green, app launches clean. Live QA pending.

## Risks / watch-items

- **Physical-vs-logical is the subtle part.** Get `Gesture`/`GestureParser`/precedence right in Phase 1
  or the Croatian-layout fold/comment regresses silently (can't catch headlessly). Add explicit tests
  feeding both `Key` and `PhysicalKey`.
- **Tunnel vs bubble ordering** must be preserved so editor/grid still win over the built-in controls.
  Keep the existing `AddHandler(..., Tunnel)` registrations; only the body changes.
- **Alt-tap menu toggle** isn't a normal gesture (it's key-up with no other key). Keep its bespoke
  `OnKeyUp` logic; model it as a command the toggle invokes, not as a resolvable gesture.
- **Don't over-scope AvaloniaEdit.** Leave its built-ins alone; document the boundary in the palette
  (e.g. a non-rebindable "Editor built-ins" note) so users aren't confused about why Ctrl+Z isn't listed.
```
