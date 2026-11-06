# Spec — Inline results in the editor

Date: 2026-11-06 · Branch: `inline-results` · Scope: **feature, multi-file (App + one Core enum)**

Revision 2 (2026-11-06, after eyeball QA): **full parity with the results pane.** Revision 1 shipped a
read-only inline table and sent editing to the pane; that scope cut was never put to the user and is
withdrawn. The decisions below were settled one by one with the user (grill-me, Q1–Q10).

## Goal

An option to show query results **inside the SQL editor, directly beneath the statement that produced them**
(notebook / DataGrip style), as an alternative to the results pane. Each inline zone is **the same results
view as the pane** — editing, save / discard / show SQL, FK navigation with Back, export, load more / count /
fetch all, inspector, sorting, column freeze / hide, Stacked / Tabs — hosted in the editor.

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
     our split → the unattributed single-zone fallback.
   - Run All splits per statement only when every set is numbered **and** there are as many sets as our split
     found statements — the same two checks `ResultSetBuilder.StatementsBehind` makes.
   - A statement's zone hangs under its last line of code, not under comment lines the split attached to it
     (`ISqlDialect.CodeEnd`, each engine's own lexer).
3. **Lifetime**
   - A Run replaces any zone anchored inside the executed span, **excluding** one sitting exactly at its
     start (the previous statement's, when nothing separates them); other zones stay.
   - Run All replaces every zone in the tab.
   - ✕ removes that zone.
   - Editing text moves zones with it (anchored, not line-numbered). Editing the statement itself keeps
     its zone (stale results stay until re-run, as in DataGrip).
   - **Deleting the text a zone is anchored to:** a zone *without* pending edits goes with it. A zone *with*
     pending edits survives at the deletion point, its header reads *"statement removed"*, and Save / Discard
     still work (Q4).
   - Zones are per tab and survive tab switches; **not persisted** across restarts (results never are).
   - Different text loaded into the same tab (File ▸ Open into it, a reload from disk) drops its zones —
     behind the pending-edits guard (§7).
   - The buffer stays editable during a run: the run's offsets are carried through the edits made since it
     started (document version history). If the run spanned a buffer swap with the text changed, the results
     go to the pane, which opens, instead of being placed at a guess. A run that finished on a background tab
     opens the pane when that tab is next shown.
   - Only results the run itself set are placed (`EditorTabViewModel.RunResultsStamp`) — never whatever FK
     navigation or Back put in the tab while the run was still before its statement.
   - Switching the setting to `Pane` hides all zones (kept on the tab, shown again if switched back).
4. **Each zone is its own results context** (Q1). A zone holds its current sets **and its own Back stack**;
   following an FK in a zone replaces that zone's contents in place, Back in the zone's header returns. The
   pane is the tab's context, exactly as today. Navigation history moves off `EditorTabViewModel` into a
   small context object both use. Pending edits live on `ResultSetViewModel` already, so they travel with
   the set.
5. **The zone view** is the real `ResultView` (Q7), with:
   - **One header bar**: meta (*"1,000 rows · 42 ms"*, or the batch caption) · Back · Stacked / Tabs ·
     placement toggle · ✕. The pane's dock header is not stacked on top of it.
   - Edit toolbar, stats bars, column header menus and the inspector (opens on the zone's right, the zone
     keeps its height) — unchanged from the pane.
   - Several sets from one statement follow the pane's Stacked / Tabs setting.
   - Statements without rows show a one-line zone (*"UPDATE 5"*); a failed statement shows its error line.
6. **Size**
   - **Height: fit up to a cap** (Q6). A zone is as tall as its content, never taller than the cap; the cap
     defaults to 10 rows, the bottom grip drags it (2–60 rows), remembered on the zone
     (`InlineResultZone.TableHeight`). Load more grows the zone until the cap, then the grid scrolls. In
     Stacked mode the cap applies **per set**; in Tabs mode once.
   - **Width: fit to columns up to the editor viewport** (Q8); wider results scroll horizontally inside
     their own grid. Window / viewport resize refits.
   - **Pinned to the viewport's left edge**: when the editor scrolls horizontally the zone does not slide off
     with the text (offset by the editor's horizontal scroll).
7. **Unsaved edits are never dropped silently** (Q3/Q4 — applies to the **pane too**):

   | Event | Behaviour |
   |---|---|
   | Run / Run Selection / Run All replacing results that hold pending edits (zone or pane) | Ask first: **Discard and run** / **Cancel** (Cancel is `IsDefault` + `IsCancel`). One prompt per run, naming how many zones and changes. Asked after execution's refusals and write confirmation, so a refused run never asks; an open cell editor is committed first (§9.15); the edits are reverted only once the run's results replaced them. With zones shown, a pane set that a zone the run leaves in place also holds is not counted |
   | ✕ on a zone with pending edits | Same prompt, counting only sets the pane or another zone doesn't also hold |
   | A zone's placement toggle over pane frames holding edits no zone holds | Same prompt |
   | FK navigation away | No prompt — the frame keeps its edits on the Back stack |
   | Tab close / quit | Joins the existing ask-now-act-later close flow |
   | Switching to Pane mode | No prompt — zones are hidden, edits kept |
   | Deleting the statement's text | No prompt; the zone survives (§3) |
   | Script reloaded from disk, another script opened into the tab | No prompt (a clean reload happens on its own); zones holding edits are kept, marked *statement removed* (`ClearInlineZones`) |
   | Moving the tab to another project | Refused while edits are pending — the existing `ExternalScripts` refusal now counts zones too. A move drops the tab's zones with its pane results: they came from the old project's connection |
8. **Focus and keys** (Q2/Q3)
   - **In**: clicking a cell focuses that zone's grid. In Inline mode `focus.results` (Ctrl+Shift+0) focuses
     the zone of the statement under the caret, else the nearest zone above it; F6 cycles editor → that
     zone → sidebar.
   - **Out**: Esc in the grid (no cell editor open; the rebindable `grid.leaveInline`) returns focus to the
     editor, caret at the end of the zone's statement; an open cell editor takes the first Esc (§9.15).
     Clicking the SQL text leaves the zone and commits an open cell edit, the same rule as focus leaving the
     pane's grid.
   - Arrow keys never leave the grid.
   - **No key or text input from a zone reaches the document** — neither the editor's tunnel handler, nor
     AvaloniaEdit's own key bindings / text input.
   - Grid-scoped commands act on **the focused grid** (pane or any zone), not on the pane's instance.
   - **Run (Ctrl+Enter / F5) with focus in a zone re-runs that zone's statement** and replaces the zone in
     place; Explain and other caret-scoped run commands likewise. Run All is unchanged. Ctrl+S in a grid is
     save edits (grid scope resolves before global), as in the pane.
   - Caret (unchanged): Up/Down step over a zone's row; Shift+Up/Down extend only to the statement line's
     end (known limitation).
9. **Placement toggle** (⤢ is **removed**, Q5). Only one placement is ever drawn, so each set is in exactly
   one live grid.
   - Pane header → Inline: results move under their statements; the pane collapses.
   - Zone header → Pane: the pane shows that zone's context (sets, pending edits, Back stack).
10. **Lifecycle of the view** (Q9): a zone's `ResultView` is built the first time its line is drawn, then
    kept for the zone's life (scrolled off-screen, tab switches) so it returns as left — scroll, selection,
    frozen / hidden columns, inspector, Back stack. Released when the zone dies (✕, replaced by a run, tab
    closed, cleared by a reload). No cap on zone count.

## Mechanism (verified against AvaloniaEdit 12.0.0 source)

A `VisualLineElementGenerator` returns a zero-length element at the anchored line's end; the element emits
`TextEndOfLine` then an `InlineObjectRun` hosting the zone control, so the control gets its **own row**
inside that visual line and following lines move down. `HeightTree` is internal — this is the only
supported way to reserve vertical space. The control is a real visual child (input works). Inline objects
are measured with infinite space, so the zone sets an explicit width and height (§6) — a DataGrid must
never be measured unbounded. Anchoring uses `TextAnchor` while the tab is bound (`SurviveDeletion` true only
while the zone has pending edits); offsets are written back to the tab VM on unbind, because one
`TextEditor` serves every tab and its document text is replaced on switch.

## Change manifest

Revision 1 files stay (see the JSON manifest). Revision 2, as built:

New:
- `src/Bearing.App/ViewModels/ResultFrames.cs` — `IResultFrames` (a results context: current sets + Back
  stack; the tab and each zone implement it), `ResultHistory`, `PendingEdits`
- `src/Bearing.App/Controls/InlineZoneView.cs` — a zone in the editor: hosts the real `ResultView`, the grip,
  and keeps keys / text / presses / focus from reaching AvaloniaEdit (`IsInside` for the editor's own tunnel
  handlers)
- `src/Bearing.App/Controls/GridCommandRouter.cs` — the registered grid commands act on the focused view
- `src/Bearing.App/Results/InlineGridSizing.cs` — pure: fit-up-to-cap heights
- `src/Bearing.App/Editing/PendingEditsGuard.cs` — pure: what a run / ✕ / tab close would lose, and the ask
- `src/Bearing.App/Views/ResultViewWiring.cs` — the pane's results-view callbacks, applied to any instance
- `src/Bearing.App/Views/ConfirmDiscardEditsDialog.cs` — the prompt (code-built, like its siblings)

Modified:
- `src/Bearing.App/Controls/ResultView*.cs`, `ResultChrome.cs` — inline mode (one header bar, per-grid
  height, stacked without the splitter), routed grid commands; thin members only (§0.4)
- `src/Bearing.App/Editing/InlineResultsController.cs` — hosts `InlineZoneView`, keeps each view for its
  zone's life, horizontal pin, keeps a zone with edits when its text is deleted, focus in / out
- `src/Bearing.App/Editing/InlineResultsCoordinator.cs` — builds zone views, run-in-zone, guard before a run
  and ✕, placement toggle carrying the context, focus.results
- `src/Bearing.App/Editing/EditorTextCommands.cs` — `ScopeForZone`, `StatementBounds`
- `src/Bearing.App/Editing/EditorAutoClose.cs`, `EditorContextMenu.cs` — skip events from a zone
- `src/Bearing.App/Input/FocusRing.cs` — the innermost container holding focus is the current region
- `src/Bearing.App/ViewModels/EditorTabViewModel.cs`, `InlineResultZone.cs` — history on `ResultHistory`,
  `ShowZoneInPane`, `PendingEditCount`, `ClearInlineZones` keeps zones with edits
- `src/Bearing.App/ViewModels/ExecutionViewModel.cs` — FK navigation pushes into the given context
- `src/Bearing.App/ViewModels/WorkspaceViewModel.cs`, `Views/QuitGuard.cs` — close / quit ask about edits
- `src/Bearing.App/Services/IDialogService.cs`, `Views/DialogService.cs` — `ConfirmDiscardEditsAsync`
- `src/Bearing.App/ViewModels/ShellViewModel.cs` — exposes its `Dialogs`
- `src/Bearing.App/Workspace/ExternalScripts.cs` — move refusal counts zones
- `src/Bearing.App/Views/MainWindow.*` — wiring only (§0.4)

Removed:
- `src/Bearing.App/Controls/InlineResultView.cs` — the read-only table, and ⤢

Not touched: execution, write guard, refusals, providers, session persistence.

## Test manifest

Kept: `InlineResultPlacementTests`, `InlineRunPlacementTests`, `InlineRunPathTests`, `ScriptFileSyncTests`,
`DialectCodeEndTests` (adjusted where ⤢ was used). `InlineResultViewTests` is replaced.

New:
- `tests/Bearing.App.Tests/ResultContextTests.cs` (pure / VM): zone FK frames and Back, the pane untouched, the
  toggle carrying frames and edits, refused while running, the guard's counting (shared sets once, only
  replaced zones), discard reverts, zones with edits survive new text, sizing, batch zones marked.
- `tests/Bearing.App.Tests/Ui/InlineZoneGridTests.cs`: an editable realized grid with the edit toolbar;
  short vs capped heights; an added row grows the grid; Stacked caps per set; width ≤ viewport and narrow
  fits; sideways scroll keeps the left edge; a tab switch returns the same view with its selection.
- `tests/Bearing.App.Tests/Ui/InlineZoneInputTests.cs`: a click focuses the zone's grid and leaves the caret;
  typing / arrows / Backspace / Delete / Enter / a cell editor leave the SQL unchanged; Esc → editor at the
  statement end; Esc in a cell editor cancels it and stays; Ctrl+Shift+0 and F6 reach the caret's zone;
  Ctrl+A acts on the focused zone, not the pane; Run in a zone re-runs its statement and focus follows.
- `tests/Bearing.App.Tests/Ui/PendingEditsGuardTests.cs`: a run over edits asks; Cancel keeps; Discard
  reverts and runs; a run elsewhere doesn't ask; the pane asks too; ✕ asks; deleting the statement keeps a
  zone with edits; closing the tab asks.

## Out of scope

Persisting zones across restarts; a default key binding for the toggle; per-statement attribution on SQL
Server (needs provider work).

## Risks (unverified until the headless tests run)

- AvaloniaEdit's `TextArea` input handlers acting on keys / text input that bubble out of a zone.
- DataGrid measure / virtualization inside an inline object (explicit size should hold).
- F6 / focus-region detection treating a zone (inside `Editor`) as the editor.
- Many live grids after a large Run All (memory).
- Zones inside a fold disappear with it (accepted).
- Visual look needs your eyeball QA (§0.2).
