# Spec — Inline results in the editor

Date: 2026-11-06 · Branch: `inline-results` · Scope: **feature, multi-file (App + one Core enum)**

## Goal

An option to show query results **inside the SQL editor, directly beneath the statement that produced them**
(notebook / DataGrip style), as an alternative to the results pane. Each inline block is a compact,
read-only table with **✕ close** and **⤢ open in pane**.

## Behaviour

1. **Setting** `results.placement` — `ResultsPlacement { Pane, Inline }`, default `Pane` (no change for
   existing users). Results category in Settings. Command `view.toggleInlineResults` ("Toggle Inline
   Results") in the View menu and palette; **no default key binding**.
2. **Where a result lands** (decided by a pure helper, `InlineResultPlacement`):
   - **Run** (statement under caret or selection): every result set of that run goes in **one zone at the
     end of the executed span** (statement end / selection end).
   - **Run All**: one zone per statement **only when the server proved the mapping**
     (`QueryResult.StatementIndex` on every set — Postgres when set count = statement count; demo).
     Otherwise (SQL Server always, mixed writes/selects on Postgres) **all sets go in one zone at the end
     of the last statement**, captioned *"N result sets from this batch"*. We never guess a per-statement
     mapping (§1.1).
   - `StatementIndex` counts the **non-empty** parts `EnsureSeparated` sent, not raw splitter spans
     (empty spans are dropped there); the helper maps through the same filter. A statement number outside
     our split → the unattributed single-zone fallback. (A provider and our lexer disagreeing on the count
     while every number stays in range is not detectable here; on `EnsureSeparated` output they agree.)
3. **Lifetime**
   - A Run replaces any zone anchored inside the executed span; other zones stay.
   - Run All replaces every zone in the tab.
   - ✕ removes that zone. Deleting the text the zone is anchored to removes the zone.
   - Editing text moves zones with it (anchored, not line-numbered). Editing the statement itself keeps
     its zone (stale results stay until re-run, as in DataGrip).
   - Zones are per tab and survive tab switches; **not persisted** across restarts (results never are).
   - Different text loaded into the same tab (File ▸ Open into it, a reload from disk) drops its zones.
   - The buffer stays editable during a run: the run's offsets are carried through the edits made since it
     started (document version history). If the run spanned a buffer swap (tab switch, reload) with the text
     changed, they can't be, and the results go to the pane, which opens, instead of being placed at a guess;
     with the text unchanged the offsets still hold and the results are placed. A run that finished on a
     background tab opens the pane when that tab is next shown. (Amended.)
   - Only results the run itself set are placed (`EditorTabViewModel.RunResultsStamp`) — never whatever FK
     navigation, ⤢ or Back put in the tab while the run was still before its statement (row count, write
     confirmation). (Amended.)
   - A Run replaces the zones inside what it ran, **excluding** one sitting exactly at its start (the
     previous statement's, when nothing separates them). (Amended.)
   - Run All splits per statement only when every set is numbered **and** there are as many sets as our split
     found statements — the same two checks `ResultSetBuilder.StatementsBehind` makes. (Amended.)
   - A statement's zone hangs under its last line of code, not under comment lines the split attached to it
     (`ISqlDialect.CodeEnd`, each engine's own lexer). (Amended.)
   - Switching the setting back to `Pane` hides all zones (kept on the tab, shown again if switched back).
4. **Results pane in Inline mode**: the tab still holds `Results` (single source of truth for editing,
   FK navigation, export, Back). The pane is **not auto-revealed** after a run; Ctrl+R still toggles it.
   **⤢** puts that zone's sets in the pane (`tab.SetFreshResults`) and reveals it. Refused while the tab is
   running (the run's results would otherwise be confused with the zone's); the pane's Back is refused
   while running for the same reason. All editing, sorting,
   FK navigation and export happen in the pane.
5. **The inline table**: header row (column names), up to **10 visible rows** then its own vertical
   scroll, width = the editor viewport; a meta line in its header bar, beside ✕ and ⤢, *"1,000 rows · 42 ms"*
   (+ *"more in pane"* when the result is pageable). Cells rendered through `ResultCellFactory.Shown` (so digit grouping and
   timestamps match the grid — no new `NumberGrouping` caller, §9.10c). Statements without rows show a
   one-line zone (*"UPDATE 5"*). A failed statement shows its error line inline too (amended during
   implementation: the pane no longer opens by itself in Inline mode, so an error left only there would be
   invisible).
6. **Input**: nothing in a zone takes focus; clicks on it never reach the document (caret placement, text
   selection are handled at the zone root). (Mouse-wheel behaviour is the default nested-scroll one and is
   not asserted.)
7. **Caret** (amended): Up/Down step over a zone's row — it belongs to the statement line's visual line and
   has no caret stops, so without help Down could never get past a zone. Shift+Up/Down extend the selection
   only to the statement line's end (known limitation).

8. **Placement toggles and resizing** (added after first eyeball QA, 2026-11-06):
   - The results pane header has an inline toggle: switch to Inline and collapse the pane. Runs are placed in
     both modes (Pane mode just doesn't draw them), so the results that were in the pane appear under their
     statements.
   - Each zone has a pane toggle: switch to Pane and show that zone's sets there.
   - A grip on a zone's bottom edge drags its tables' height (2–60 rows), remembered on the zone
     (`InlineResultZone.TableHeight`). Hosts are reused per line so a drag survives the line being re-measured.

## Mechanism (verified against AvaloniaEdit 12.0.0 source)

A `VisualLineElementGenerator` returns a zero-length element at the anchored line's end; the element emits
`TextEndOfLine` then an `InlineObjectRun` hosting the zone control, so the control gets its **own row**
inside that visual line and following lines move down. `HeightTree` is internal — this is the only
supported way to reserve vertical space. The control is a real visual child (input works). Anchoring uses
`TextAnchor` (`SurviveDeletion = false`) while the tab is bound; offsets are written back to the tab VM on
unbind, because one `TextEditor` serves every tab and its document text is replaced on switch.

## Change manifest

New:
- `src/Bearing.Core/Workspace/ResultsPlacement.cs` — enum (Core: no references added, §2.1)
- `src/Bearing.App/Results/InlineResultPlacement.cs` — pure: spans + result sets → zones
- `src/Bearing.App/ViewModels/InlineResultZone.cs` — zone model (id, offset, sets, caption)
- `src/Bearing.App/Editing/InlineResultsController.cs` — generator/element, anchors, bind/unbind, control cache
- `src/Bearing.App/Controls/InlineResultView.cs` — the compact read-only table + header actions

Modified:
- `src/Bearing.Core/Workspace/AppSettings.cs`, `SettingsCatalog.cs` — the setting
- `src/Bearing.App/ViewModels/ResultSetViewModel.cs` — carry `StatementIndex` (copied in its constructor;
  `ResultSetBuilder` needed no change)
- `src/Bearing.App/Controls/ResultCellFactory.cs` — `DisplayText`, so inline cells format through `Shown`
- `src/Bearing.Sql/ISqlDialect.cs`, `PostgresDialect.cs`, `SqlServerDialect.cs` — `CodeEnd` (default null)
- `src/Bearing.App/Workspace/ScriptFileSync.cs`, `ViewModels/WorkspaceViewModel.cs`,
  `Workspace/ExternalScripts.cs` — clear a tab's zones where its text is replaced (reload from disk, open
  into the tab, a moved tab's edits carried onto the target's tab)
- `src/Bearing.App/Editing/InlineResultsCoordinator.cs` — placement setting, placing a finished run, the
  placement toggles, ⤢, and when the pane opens (owed reveals); kept out of `MainWindow` (§0.4)
- `src/Bearing.App/ViewModels/ExecutionViewModel.cs` — a run's results go through `SetRunResults`
- `src/Bearing.App/ViewModels/EditorTabViewModel.cs` — `InlineZones`, replace/remove
- `src/Bearing.App/Editing/EditorTextCommands.cs` — return executed spans with the SQL
- `src/Bearing.App/Views/MainWindow.Commands.cs`, `MainWindow.axaml.cs`, `MainWindow.axaml` — wiring only
  (construct the coordinator, toggle command + menu item; the pane-visibility decision is the coordinator's) (§0.4)
- `src/Bearing.App/Input/CommandIds.cs` — `view.toggleInlineResults`

Not touched: execution, write guard, refusals, providers, session persistence.

## Test manifest

- `tests/Bearing.App.Tests/InlineResultPlacementTests.cs` (pure): Run → one zone at span end; selection
  end; Run All with indexes → per statement; any null index → one unattributed zone at last statement;
  empty spans skipped in the index mapping; count mismatch → fallback; no-row results.
- (in the same file) the tab's zone list: Run replaces zones inside the span only; Run All
  replaces all; close removes; offsets round-trip through tab switch.
- Settings: `SettingsCatalogTests` covers the new key (existing enumerations).
- `tests/Bearing.App.Tests/Ui/InlineResultsTests.cs` (headless, ShellHarness + demo provider): zone
  realized as a TextView child placed below its statement's text and above the next line; ✕ removes it;
  typing with a zone control focused leaves the document unchanged; editing above the statement moves the
  zone; tab switch away and back restores it; pane stays collapsed in Inline mode; ⤢ fills and reveals
  the pane; Pane mode shows no zones.

## Out of scope

Editing / sorting / FK navigation / export inside the zone; persisting zones; a default
key binding; per-statement attribution on SQL Server (needs provider work).

## Risks (unverified until the headless tests run)

- Caret Up/Down landing on the zone row; End drawn one column past the text.
- Whether a zone's size change re-measures its visual line on its own (fallback: `Redraw(line)`).
- Editor key bindings (tunnel `KeyDown`) grabbing keys meant for the zone.
- Zones inside a fold disappear with it (accepted).
- Visual look needs your eyeball QA (§0.2).
