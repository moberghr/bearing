# Editor 4a — QA feedback TODO (round 1, 2026-07-18)

User's live-QA feedback on the completed 6-phase redesign. Grouped into batches; each batch is one
commit on branch `editor-4a-redesign`. Status: `[ ]` todo · `[~]` in progress · `[x]` done.

## Batch A — removals + rail toggle + quick fixes  (DONE)
- [x] Remove **focus mode** entirely (overlay, FocusEditor, IsFocusMode, F11/Ctrl+Alt+F, Focus buttons).
- [x] Remove the dedicated **open-sidebar (☰) button** from the toolbar.
- [x] Clicking a rail icon **toggles the sidebar open/closed** (re-click active tile collapses it).
- [x] **Database pill shows blank** for the default DB — `SelectedTabDatabase` getter now falls back to the connection's DB.
- [x] Scripts view **doesn't show empty folders** — folders now shown when unfiltered even if empty.
- [x] Rename **Schema** view → **Connections**; rail tile uses the database icon.
- [x] Tree node icons: **servers** → server icon (#7E9CD8), **databases** → cylinder icon (#98BB6C), via `IconKey`/`ResourceGeometry`.

## Batch B — scripts panel
- [ ] **Nested subfolders** (recursive tree, not just one level).
- [ ] **Create subfolders** (new-folder inside a selected folder).
- [ ] **Drag & drop** scripts between folders (moves the file on disk).
- [ ] **Keyboard navigation** (up/down + Enter to open).

## Batch C — connections/schema tree search  (DONE, shipped with A)
- [x] **Type-ahead**: typing fuzzy-searches realized nodes and jumps selection to the next match (cycles).
- [x] **Highlight all matches** (translucent orange, `IsMatch` + `MatchHighlight`); Esc/Backspace clear.
      Note: only searches *realized* (expanded/loaded) nodes; collapsed DBs' tables aren't matched until expanded.

## Batch D — history panel
- [ ] Single-click a history row shows a **preview** of the query before double-click opens it in a new scratch tab.

## Batch E — execution
- [ ] Selecting **two blank-line-separated statements** (first has no `;`) and running → PG syntax error.
      Decision: **auto-split** the run text via `StatementSplitter` and send statements properly separated
      (consistent with the editor's statement model), rather than forcing semicolons.

---
Deferred refinements from the original plan still stand (see `docs/editor-4a-plan.md` tail):
exact Kanagawa syntax theme, custom Popup dropdowns, retire unused `HistoryWindow`, etc.
