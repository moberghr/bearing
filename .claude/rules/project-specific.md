# Project-Specific Rules (§9.x)

## §9.1 — God objects: extract, don't grow
Three files are already oversized and are the #1 place changes go wrong:
- `src/Squirrel.App/Controls/ResultView.cs` (~1700 lines)
- `src/Squirrel.App/Views/MainWindow.axaml.cs` (~1600 lines)
- `src/Squirrel.App/ViewModels/MainWindowViewModel.cs` (~1000 lines)

WHEN a change would add code to any of them, DO NOT append — extract:
- Pure/stateless logic → helpers under `Results/`, `Input/`, or the `Sql` project (pattern:
  `ResultSetBuilder`, `ResultEditModel`, `PaletteFilter`).
- Self-contained visuals/overlays → their own `Views/`/`Controls/` class.
- Stateful coordination (connections/execution/tabs/panels) → a dedicated coordinator in `Connections/`.

Keep the VM's public binding surface unchanged when splitting — leave thin delegating members behind.

## §9.2 — Input goes through the unified pipeline
- Keyboard handling flows through `src/Squirrel.App/Input/` (`Gesture`/`GestureParser`, `Keymap`,
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
