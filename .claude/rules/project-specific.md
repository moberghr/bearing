# Project-Specific Rules (§9.x)

## §9.1 — God objects: extract, don't grow
**All three god objects were decomposed (2026-08-10). Keep them that way.** Splitting a class into partial
*files* is what previously hid the problem: the line count moved, the concerns didn't. Extract *types*.

The three, and the pattern each now demonstrates:
- `ViewModels/ShellViewModel` (was `MainWindowViewModel`, 1,014) → child VMs behind `WorkspaceContext`.
- `Controls/ResultView.*.cs` (1,902 → ~500 over 3 partials) → a composition root that assembles
  `GridSelectionController`, `ResultCellFactory`, `ResultChrome`, `CellInspectorView` + `InspectorPane`,
  `ResultEditToolbar`, `ResultGridChrome`, `QuickStatsBar`, `ResultRowPainter`, plus the pure
  `Results/{GridSelectionOps, ResultMetaText, ColumnKinds}`.
- `Views/MainWindow.*.cs` (1,167 → ~820 over 5 partials) → `.Commands` is the command table + key routing,
  `.Chrome` the XAML-wired handlers; behavior lives in `Editing/EditorTextCommands`, `EditorChrome`,
  `Views/CommandPaletteHost`, `Views/ResultsPaneController`, `Input/{TabNavigator, FocusRing}`.

WHEN a change would add code to any of these, DO NOT append — extract:
- Pure/stateless logic → helpers under `Results/`, `Input/`, or the `Sql` project (pattern:
  `GridSelectionOps`, `ResultSetBuilder`, `ResultEditModel`, `PaletteFilter`). This is also the only way to
  get the behavior under test — Wayland blocks headless GUI testing (§4.3).
- Self-contained visuals/overlays → their own `Views/`/`Controls/` class.
- Stateful coordination (connections/execution/tabs/panels) → a dedicated coordinator.

Keep the public binding / callback surface unchanged when splitting — leave thin delegating members behind.

Code-built visuals resolve token brushes through `Controls/Tokens` (`Res` / `Tint`) — do not reintroduce a
private `FindResource` helper; six of them were consolidated. `Theming.ThemeBrush.AtAlpha` is the one
exception (it takes an explicit fallback colour, for converters and custom margins).

## §9.2 — Input goes through the unified pipeline
- Keyboard handling flows through `src/Bearing.App/Input/` (`Gesture`/`GestureParser`, `Keymap`,
  `CommandRegistry`/`KeyCommand`, `KeyDispatcher`, `CommandIds`, `KeyScope`). Views call `TryHandle(e, scope)`.
- WHEN adding a shortcut, register a command + default binding in the keymap — do NOT hand-roll a new
  `OnKeyDown` branch. Grid **spatial** navigation (cursor motion) is the deliberate exception and stays local.
- User overrides load from `keybindings.json` (VS Code-style array) via `KeymapLoader`, layered over
  `KeymapDefaults`.

## §9.3 — Avalonia specifics
- Avalonia is **v12.1**. Drag & drop uses the typed in-process `DataTransfer`/`DataFormat` API
  (`DataObject`/`DoDragDrop` are obsolete → `DoDragDropAsync`).
- Theming: token brushes in `Themes/Tokens.axaml`, dark Kanagawa variant in `App.axaml`; the per-connection
  accent is mutable via `App.SetConnectionAccent(hex)`.

## §9.4 — Sessions are keyed by connection Id
- Two tabs on the same server but different databases share one session and reconnect on switch. Keep this
  in mind before assuming per-(conn,db) isolation for query sessions (the `SchemaBrowser` cache *is*
  per-conn+db; the query session manager is not).

## §9.5 — antlr4-c3 completion
- The vendored `antlr4-c3` `CodeCompletionCore` is used for SQL completion. There is a known gotcha noted in
  project memory — verify completion behavior against the existing `CompletionEngine` tests when touching it.
