# Plan — Inline results in the editor

Spec: `docs/specs/2026-11-06-inline-results.md`. Each batch ends with `dotnet build` + the App tests it
touches; the full `dotnet test` runs after batch 4.

## Batch 1 — Setting and provenance (no UI)
- `ResultsPlacement` enum; `AppSettings.ResultsPlacement` (default `Pane`); `SettingsCatalog` entry
  `results.placement` in the Results category.
- `ResultSetViewModel.StatementIndex` filled by `ResultSetBuilder` from `QueryResult.StatementIndex`.
- `EditorTextCommands`: `SqlToRun`/`SqlToRunAll` also return the executed `StatementSpan`s (buffer
  offsets; selection → one span). Existing string callers unchanged.
- Tests: settings key; builder carries the index.

## Batch 2 — Placement and zone model (pure + VM)
- `InlineResultPlacement.Assign(runKind, spans, sets)` → `IReadOnlyList<InlineResultZone>`, with the
  non-empty-part index mapping and the unattributed fallback.
- `InlineResultZone` (id, `Offset`, `Sets`, `Caption`, `Attributed`).
- `EditorTabViewModel.InlineZones` + `ApplyRun(zones, replaceRange | all)`, `RemoveZone(id)`,
  `UpdateOffsets(...)`.
- Tests: `InlineResultPlacementTests`, `InlineZoneTests`.

## Batch 3 — Editor mechanism and the zone control
- `InlineResultView`: header (columns), ≤10 rows then scroll, footer (rows · elapsed · "more in pane"),
  ✕ and ⤢ buttons; root handles `TextInput`/`PointerPressed`; explicit `Width` bound to viewport,
  bounded `Height` (infinite-measure pitfall). Cells via `ResultCellFactory.Shown`. Brushes via `Tokens`.
- `InlineResultsController`: generator + zero-length element (`TextEndOfLine` + `InlineObjectRun`),
  `TextAnchor` per zone, `Bind(tab)` / `Unbind()` writing offsets back, control cache per zone id,
  `Redraw` on zone add/remove/resize, visibility by setting.
- Tests: first headless tests (zone realized below its line; ✕; typing doesn't edit).

## Batch 4 — Wiring
- `RunAsync`/`RunAllAsync`: capture spans before executing; after results land and placement is
  Inline, `tab.ApplyRun(...)`; don't auto-reveal the pane in Inline mode.
- `MainWindow.axaml.cs`: construct the controller, bind on tab switch, react to the setting.
- `view.toggleInlineResults` command + View menu item.
- ⤢: `tab.SetFreshResults(zone.Sets)` + `RebuildResults` + reveal pane.
- Tests: rest of `InlineResultsTests` (anchor moves on edit, tab switch restores, pane collapsed, ⤢,
  Pane mode shows none).

## Then
Spec-drift check → compliance review → test + architecture reviewers (parallel) → fixes → simplify →
lessons. Eyeball QA by you before merge.
