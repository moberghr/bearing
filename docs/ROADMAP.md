# Bearing roadmap

Single tracking list for outstanding work. Grouped by status, then priority (**P1** correctness/security ·
**P2** UX/robustness · **P3** cleanup). Check items off as they land. Sources: the 2026-07-20 whole-codebase
review, the earlier hardening review, and feature requests.

Legend: `[ ]` open · `[~]` in progress · `[x]` done.

> **Reconciled against the tree + git log on 2026-08-06.** Every open item below was re-checked at its cited
> location and the file:line references refreshed; items that had quietly landed are marked done with what
> closed them. The working tree is clean — **everything described here as done is committed on `main`.**
> Verified this pass: clean build, **3 warnings**; tests **Sql 159 · App 184 · Persistence 14** green, plus
> **Data 14 all skipped** (no live Postgres — run `BEARING_TEST_PG_PORT=5434 dotnet test` to exercise them).
> Items tagged *(live QA)* are committed but never eyeball-checked in the running app (§4.3 — Wayland).

---

## ✅ Landed on `main`

- [x] **Whole earlier review batch** — squashed in `b2ada7d` (review-fixes) with follow-ups `8e4f48f`,
  `33a3fda`: resume-last-project (`App.ResumeLastProjectAsync`), server-side first-page `LIMIT`
  (`Bearing.Sql/FirstPageLimiter.cs`), honest wall-clock query timer, extended `WriteGuard`
  (CREATE/CTAS, COPY, CALL, DO, GRANT/REVOKE, REFRESH, top-level `SELECT … INTO`), connection lease +
  30-min idle eviction (`SessionLease`), and global error handling (`CrashLog`, `CrashReporter`,
  `Views/ErrorDialog`, dispatcher/AppDomain/TaskScheduler backstops).
- [x] **Squirrel → Bearing rename** — `f01ab71`. Details in the features section below.
- [x] **Pluggable credential sources** — `1aa54f7` (creds-management). Per-connection `CredentialKind`
  (`StoredPassword` / `Prompt` / `EntraToken`), `CredentialResolver` with in-memory-only caching of prompted
  passwords and minted tokens, `EntraTokenProvider` shelling out to `az account get-access-token`,
  expiry-driven disconnect before a token goes stale, and one-shot auth-retry. *(live QA)*
- [x] **Manual connect / disconnect + connection status** — `419bd68`, `d233bdb`, `bf70b6b`, `4eadb4b`.
  Toolbar status dot + label (green Connected / amber Connecting / red Disconnected — semantic, never the
  environment colour) mirrored in the status bar, plus a chain toggle that Connects / Cancels-connecting /
  Disconnects. Reflects the real session pool via `IConnectionSessionManager.LiveChanged`, so a query-driven
  connect or an idle eviction updates it too. No connect-on-tab-switch: connecting is explicit, or implied by
  Run (which now loads the schema before building results so first-page edits work). `ConnectionState` +
  state machine in `ConnectionsViewModel`; reusable `Controls/ConnectionStatusView`. *(live QA)*
- [x] **MVVM decomposition of the shell VM** — `6684644` (mvvm-fix) + `46f4024`. See the god-object item.

---

## 🚧 Next feature — Stage E: background execution

Design: `docs/background-execution-plan.md`. Scope (confirmed): concurrent per-tab queries that survive
project switches + a completion toast (**no** background-jobs panel), per-tab cancel, quit-confirm.

**Two of the five already landed** — per-tab execution is real today; what's missing is surviving a project
switch and telling you about a finish you didn't watch.

- [x] **P2** Per-tab run state + concurrent `ExecuteAsync` — done. Each tab owns its `_runCts` +
  `IsRunning` + run clock (`EditorTabViewModel.cs:99-135`), and `ExecutionViewModel.IsBusy` /
  `RunButtonText` are now an explicit **façade over the selected tab** (`ExecutionViewModel.cs:51-92`),
  re-raised on selection change and on the watched tab's `IsRunning`. Background tabs run concurrently and
  independently; the global gate is gone.
- [x] **P2** Per-tab cancel (Esc / stop button) — done, via the same per-tab CTS
  (`EditorTabViewModel.cs:135`); Esc and the Run/Cancel button act on the selected tab
  (`MainWindow.Commands.cs:41`, `MainWindow.Chrome.cs:95`).
- [ ] **P2** App-lifetime sessions — still open: `ShellViewModel.Projects.cs:91,103` calls
  `_sessions.CloseAllAsync()` on project switch, so a query dies when you change project. This is the one
  remaining piece of "queries survive project switches", and it has to land before the toast is worth much.
- [ ] **P2** Completion toast — still open, and it's the whole notification story: there is **no**
  `WindowNotificationManager` anywhere in `src/` yet. Per-tab result routing already works (results attach to
  the originating tab's VM); what's missing is telling the user about a run that finished while they were
  looking elsewhere. *(live QA)*
- [ ] **P2** Quit/tab-close confirm-then-cancel when a query is running — still open: no `OnClosing`
  override exists in `MainWindow`, so quitting mid-query just drops it. Note this now overlaps the scratch-tab
  save-on-close prompt below — both need the same window-closing hook, so build the hook once. *(live QA)*

---

## 🟢 Open — features & UX requests

- [x] **P2** Rename the product Squirrel → **Bearing** — **done 2026-08-04, committed in `f01ab71`**
  *(live QA)*. In two passes:
  - **Skin + identity.** Bearing palette in `Themes/Tokens.axaml` (graphite surfaces / steel text / teal
    `Accent.Brand`, renamed from `Accent.Orange`), the ball-bearing mark (`Themes/Brand.axaml` +
    `assets/brand/bearing-*.svg` + rebuilt `.ico`/`.icns`/favicons, sizes ≤24px using the `markSolid`
    variant), and every user-facing display string. Handoff archived at `docs/design/bearing/` with a note
    on where the app deliberately diverges from the spec. **Colour semantics were kept, not re-mapped** —
    status stays green/amber/red (`Ok.Green` holds `#98BB6C`, not the handoff's mint, which collided with
    the teal accent) and env presets keep their existing hexes.
  - **Full code rename.** `Squirrel.*` → `Bearing.*` across namespaces, assemblies, project/folder names
    and `Bearing.slnx`; `SquirrelPaths`/`SquirrelJson`/`SquirrelCompletionData` →
    `Bearing*`; `SQUIRREL_*` env vars → `BEARING_*`; `AssemblyName` → `bearing`; `build/release.sh`
    `APP_ID`/`APP_NAME`; docs and `.claude/` rules.
  - **`AppDirName` is now `bearing`**, so the app reads `~/.config/bearing` and `~/.local/share/bearing`.
    **No migration code was written — this was a deliberate call.** Existing installs must move their data
    by hand (see README ▸ Upgrading from Squirrel), and secrets stored in the OS keyring under the old
    `app=squirrel` attribute are not found under `app=bearing` — those passwords need re-entering.
  - **Not renamed:** the repo directory and git remote (still `squirrel`), and the historical design
    bundle `docs/design/editor-4a/`, left verbatim as a dated snapshot.
- [ ] **P2** Settings screen + framework. Build a real settings window (the `Settings…` menu is still a
  "coming soon" stub) backed by a general settings framework: a typed settings model, load/save via
  `AppSettingsStore`, and a UI that groups options by category. **The persistence half already exists** —
  `Core/Workspace/AppSettings.cs` + `Persistence/AppSettingsStore.cs` (atomic write, defaults on a bad file);
  what's missing is the window. Tenants already living in `settings.json` with **no UI**: query-log retention
  and `AutosaveMode` (scratch phase 3). Tenants still hard-coded or unbuilt: the 30-min idle timeout,
  TLS/`sslmode` preference, restore-window-size, base font size. Model the UI after the existing code-built
  `KeybindingsWindow` (Keyboard Shortcuts already lives under Edit ▸).

### Scratch scripts — never lose work (3 phases, agreed 2026-08-06)

Confirmed intent: (1) closing a tab must never silently drop work, file-backed *or* scratch; (2) scratch
buffers should be **real files** so they can be committed and grepped, but parked **out of the way** of the
curated scripts; (3) autosave should exist and be **configurable** (on edit / on execute / off).

**Scoping fact that shapes all three** — quitting and switching projects already lose nothing.
`BuildSession` writes `ScratchText = t.Text` for *every* tab, file-backed included, and `RestoreTabsAsync`
reloads that buffer with the on-disk text as the clean baseline, so unsaved edits come back marked modified
(`WorkspaceViewModel.cs:88-94`, `ShellViewModel.Session.cs:36-44`). The only lossy path is **explicit tab
close**: `CloseTab` (`WorkspaceViewModel.cs:66-74`) removes the tab, full stop. So the prompt belongs on tab
close, *not* on window close — prompting at quit would be friction for zero safety gain, and it leaves the
`OnClosing` hook to Stage E's running-query confirm, which is the one thing that genuinely needs it.

- [x] **P2 · Phase 1 — close prompt** — **done + user-QA'd 2026-08-06.** `CloseTab` → `CloseTabAsync`
  returning "did it actually close", gated on the new pure `EditorTabViewModel.HasUnsavedWork`
  (scratch: non-blank text; file-backed: `IsDirty`). Whitespace-only scratch counts as empty, so closing an
  untouched tab stays one keystroke. New `CloseChoice` + `IDialogService.ConfirmCloseTabAsync` +
  code-built `Views/ConfirmCloseDialog`; **`CloseChoice.Cancel` is the zero value on purpose** — a title-bar
  dismissal returns `default`, and "don't close" is the only safe reading of that. Save on a scratch tab
  routes through the destination picker, and dismissing it aborts the close rather than losing the text; a
  failed write does the same. The gate sits at the `CloseTabAsync` boundary so all three entry points
  (`Ctrl+F4`, File ▸ Close Tab, the strip's ✕) funnel through it, and the view flushes the live editor
  first so a background tab saves *its* buffer, not the focused one. New `SaveScriptAsync(tab, path, text)`
  is the per-tab save; `SaveSelectedScriptAsync` now delegates to it. 11 tests in `CloseTabPromptTests.cs`
  (+ `FakeDialogs`). **Covers the case the original item missed** — a dirty *saved* script was being dropped
  just as silently as scratch — and for that reason **does not become redundant when Phase 2 lands.**
- [x] **P2 · Phase 2 — file-backed scratch** — **done 2026-08-06** *(live QA)*. Scratch buffers live in
  `Project.ScratchDirectory` (`scripts/scratch/`) as `yyyy-MM-dd-NN.sql`, autosaved as you type, so unnamed
  work is committable and greppable. Naming a tab **promotes** it: the file moves out to the scripts root
  and the tab stops being scratch.
  - **Files are created lazily, on first non-blank content — not when the tab opens.** Deliberate departure
    from the original "at creation" wording: with no cleanup pass (by decision), eager creation would leave
    an empty file behind for every tab anyone ever opened. Opening a tab and never typing leaves no trace.
  - `IsScratch` is now a **set flag, not a derivation** from `ScriptPath` (`EditorTabViewModel`) — it's
    re-derived from folder membership (`ScratchNaming.IsUnderScratch`) by everything that repoints a path:
    save, load, rename, and the scripts-tree move/rename. Dragging a file into or out of `scratch/` changes
    what the tab *is*, not just where it lives.
  - New `Workspace/ScratchAutosave.cs` (debounced, best-effort per §5.2) + pure `Workspace/ScratchNaming.cs`
    (dated names fill gaps; membership is by folder, so `scratchpad/` doesn't false-positive). Flushed on
    tab close, project switch, and — narrowly — shutdown: `FlushExistingBlocking` only rewrites tabs that
    *already* have a file, because creating one there would raise `ScriptPath` change notifications on a
    thread that's tearing the app down. A brand-new buffer loses nothing by being skipped; its text is in
    `session.json` and it gets a file on the next keystroke after restart.
  - Scripts tree: scratch is **pinned first**, collapsed by default, dimmed file glyph + an `auto` chip
    (`ScriptFolderViewModel.IsScratch`, `SidebarView.axaml`). Its files behave like any other script.
  - **Correction to the plan above: `ScratchText`/`ScratchName` did *not* leave `OpenEditor`.** `ScratchText`
    is what carries unsaved edits for *named* scripts across a restart (see
    `Unsaved_script_edits_survive_reload_and_stay_marked_dirty`) — it was never scratch-only, so the session
    format is unchanged and pre-Phase-2 sessions restore as before, migrating to a file on the next edit.
  - **Phase 1's prompt stopped firing for scratch, exactly as predicted** — `CloseTabAsync` flushes the
    pending write and there's nothing to lose. The prompt now guards dirty *named* scripts, plus the
    backstop case of a scratch buffer that never reached a file (no project / failed write).
  - 24 tests (`ScratchNamingTests` 13, `ScratchFileTests` 11); 5 close-prompt tests rewritten for the new
    behaviour. **Not done, by decision:** retention/cleanup of abandoned scratch files, and gitignore.
- [x] **P2 · Phase 3 — configurable autosave** — **done 2026-08-07** *(live QA)*. `AppSettings.AutosaveMode`
  (`OnEdit` / `OnExecute` / `Off`), read from `settings.json`. `ScratchAutosave` generalised and renamed
  **`TabAutosave`** — it now covers named scripts too, gated on the mode instead of on `IsScratch`.
  - **`OnEdit` is the default, so named scripts now write themselves as you type.** That is the behaviour
    change to know about: under the default, a saved `.sql` never goes dirty, the tab-strip dot effectively
    never appears, and Phase 1's close prompt never fires for it. Git is the undo. Anyone who wants the old
    behaviour sets `"autosaveMode": "Off"`.
  - **Scratch is exempt from the mode.** Its file is the buffer's only home, so it is still written at the
    checkpoints that would otherwise lose it (tab close, project switch, shutdown) in **every** mode, `Off`
    included — but under `Off` it no longer writes on each keystroke, only at those checkpoints. Named
    scripts get no checkpoint writes: `FlushAsync`/`FlushExistingBlocking` both bail for them under `Off`,
    so a checkpoint can't sneak past the setting.
  - `OnExecute` fires from `ExecutionViewModel.ExecuteAsync` **after** its guards and the write-confirm, at
    the point of no return — a run blocked by "no connection" or a cancelled write-confirm never happened,
    so it doesn't save. Reached via `WorkspaceContext.Autosave` so the execution concern needn't depend on
    the workspace VM.
  - Phase 1's prompt is still correct under every mode, as required: `Off` → the guard for dirty named
    scripts; `OnExecute` → guards edits made since the last run; `OnEdit` → only the unbacked-scratch
    backstop is left. There's a test per mode.
  - 14 tests (`AutosaveModeTests`), including a `SkippableFact` that covers the `ExecuteAsync` wiring
    through a real run — the in-process tests exercise `OnExecutedAsync` directly, which can't prove the
    call site. 5 pre-existing tests now pin `AutosaveMode.Off` explicitly, since dirty state is only
    observable when nothing is autosaving.
  - **Still file-edit-only** — no UI. The settings screen below is where this mode should surface.
- [ ] **P3** **A failed schema expand is sticky until Refresh.** `EnsureChildrenAsync` sets `_loaded = true`
  *before* the load (`SchemaNodes.cs:73`), so collapsing and re-expanding a node that errored replays the
  stale error instead of retrying. Refresh server metadata does clear it, so this is a papercut, not a
  blocker — reset `_loaded = false` in the catch so re-expand retries too. Worth noting the layer below
  **already intends** the retry: `SchemaBrowser.BuildAsync` deliberately evicts a failed build so "the next
  expand retries (e.g. after fixing credentials)" (`SchemaBrowser.cs:80-86`) — the node's `_loaded` flag is
  what defeats that intent. (Surfaced 2026-08-06 while chasing an Entra connection error that turned out not
  to reproduce; this half stands on its own and is independent of credentials.)
- [ ] **P2** Remove / delete projects. Delete a project from the recent list, and optionally from disk (with confirm). Also prune stale/missing entries from the recent list (see the P3 recent-projects item below).
- [ ] **P3** Restore last window size on startup; persist size only and let the window manager handle placement/position. (Add to session or app-settings state; apply in `App`/`MainWindow`.)
- [ ] **P2** **"Show <X> panel" does nothing when the sidebar is collapsed and that panel is already the
  active one.** Reported 2026-08-07 against Scripts; the same fault covers Connections and History. The
  commands set `ActivePanel` and rely on a *side effect* to reveal the pane — `OnActivePanelChanged` does
  `SidePaneOpen = true` (`ShellViewModel.cs:117-121`). But `[ObservableProperty]`'s setter short-circuits on
  an unchanged value, so when `ActivePanel` is already `Scripts` the changed-handler never runs and the pane
  stays collapsed. Panel *switching* works, which is why it looks like "only switches focus".
  Fix: make revealing explicit rather than a notification side effect — set `ActivePanel` **and**
  `SidePaneOpen = true` at each site. Note this is *not* `ActivateOrTogglePanel`
  (`ShellViewModel.cs:110-115`): that deliberately collapses on re-activation, which is right for a rail
  tile but wrong for a command named "Show …". Five call sites, all in `MainWindow.Commands.cs` — the three
  palette commands (`:142-147`) and the two View-menu handlers `OnMenuSchemaClick` / `OnMenuScriptsClick`
  (`:61-62`). Consider a single `ShowPanel(SidePanel)` on the shell so there's one reveal path, and drop the
  implicit open from `OnActivePanelChanged` once every caller is explicit.
- [ ] **P3** Selecting a script that's already open should focus its existing tab (not open a duplicate / no-op). `OpenScriptInNewTabAsync` already focuses an existing tab on open; verify the single-click/select path in the scripts tree does the same. `ShellViewModel.Scripts` / `SidebarView`.
- [ ] **P2** Keyboard shortcuts for result-grid editing — save changes / discard / add row. Delete-row and begin-edit already have grid commands (`grid.delete`, `grid.beginEdit`); add `grid.save` / `grid.discard` / `grid.addRow` to `CommandIds` + `KeymapDefaults`, register them in `ResultView`'s grid scope (`RegisterGridCommands`), gated on `IsEditable`/`HasPendingChanges`.
- [x] **P2** **Editor line/word editing shortcuts + reclaim `Ctrl+W`** — **done + user-QA'd 2026-08-06.**
  All three land through the input pipeline (§9.2): commands in `CommandIds` + `KeymapDefaults`, no ad-hoc
  `OnKeyDown` branches. New pure helper `Bearing.Sql/TextDeleter.cs` (+`DeleteRange`) returns the *span to
  remove* rather than a rewritten buffer — unlike `LineCommenter`, which replaces the whole document — so
  the single `Document.Remove` keeps AvaloniaEdit's undo granular. 15 tests in `TextDeleterTests.cs`, 2 in
  `KeybindingTests.cs`; the `MainWindow.Commands.cs` side is ~15 lines (`EditorSpan` + `ApplyDelete`), and
  `ToggleLineComment` was refactored onto the shared `EditorSpan` helper.
  - **`Ctrl+U` = delete to the beginning of the line** (`editor.deleteToLineStart`) — **not** delete-line as
    originally scoped; readline `unix-line-discard` is what was wanted. Column 0 is the stop, not the
    indentation. With a selection the span runs from the start of the selection's *first* line to the
    selection's end, so a multi-line selection never survives in fragments. At column 0 it is a no-op.
  - **`Ctrl+W` = delete word before the caret** (`editor.deleteWordBack`) — readline `unix-word-rubout`:
    **whitespace**-delimited, not identifier-delimited, so `public.orders` dies whole. That is deliberately
    *different* from Avalonia's built-in `Ctrl+Backspace` (word characters), which is what earns it a
    binding. Trade-off: a quoted identifier containing a space takes two presses.
  - Both are **line-local by construction** — neither can join lines, so a stray `Ctrl+W` at column 0 does
    nothing instead of silently merging with the line above. Editor scope resolves on the tunnel path and
    `KeyDispatcher` sets `e.Handled` before running, so AvaloniaEdit's inherited `Ctrl+U` case-conversion
    never fires.
  - **`Ctrl+F4` = close script** — `CommandIds.TabClose` moved off `Ctrl+W` (`KeymapDefaults.cs:25`).
    `SyncMenuGestures` picks it up automatically (`DisplayGesture` → `KeyGesture.Parse("Ctrl+F4")`), pinned
    by a test. `Ctrl+W` is now unbound in Global and Grid scope, so tab-close can't fire from the grid or
    sidebar.
- [ ] **P2** **Autocomplete popup: icons, styling, and schema completion.** The engine is schema-aware but
  the popup is stock AvaloniaEdit chrome showing plain strings, and schemas are absent from completion
  entirely. Three parts, independent enough to land separately:
  - **Per-kind icons.** `BearingCompletionData.Image => null` (`Completion/BearingCompletionData.cs:17`)
    — every row renders iconless, so a table, a column, a keyword and an FK join snippet look identical.
    `SuggestionKind` (`Core/Completion/Suggestion.cs:3-15`) already exists precisely so "the UI layer
    chooses the glyph", and the engine already tags Table / View / Column / Join / Keyword
    (`CompletionEngine.cs:64,107,157,207,251`). Wire kind → glyph, giving **joins their own mark** so an
    FK-driven `JOIN … ON …` snippet reads as a distinct action rather than another table. Note
    `ICompletionData.Image` is an `IImage`, but `Themes/Icons.axaml` holds `StreamGeometry` resources
    (`Icon.Database`, `Icon.Schema`, …) — either render geometry to a `DrawingImage` or replace the
    list's item template, which is the same seam as the styling item below. Four enum members
    (`Alias`, `JoinPredicate`, `Function`, `Snippet`) are declared but never emitted — decide whether they
    get glyphs or get deleted.
  - **Style the popup to match the app.** No `CompletionWindow`/`CompletionList` style exists anywhere in
    `Themes/` — the popup ignores the Bearing tokens (graphite surfaces, teal `Accent.Brand`) that every
    other surface uses. Also fix the layout hack in `BearingCompletionData.Content:22-24`, which fakes
    two columns by padding with four spaces (`$"{DisplayText}    {DetailText}"`); a real item template
    gives icon + name + dimmed detail + right-aligned `TrailingText` (the join-predicate preview, currently
    **never displayed at all** — nothing reads `Suggestion.TrailingText`). Description tooltips already
    flow through `Description`.
  - **Complete schema names — both directions, neither works today.** `ISchemaSnapshot.Schemas` exists
    (search_path-ordered, `Core/Schema/ISchemaSnapshot.cs:13`) and is referenced **nowhere** in `src/`, and
    `SuggestionKind.Schema` is likewise never emitted. So: (a) at a table position, offer schema names
    alongside tables; and (b) typing `public.` produces an **empty popup** — `AliasQualifierBefore`
    (`CompletionEngine.cs:265-273`) matches any `ident.` and unconditionally routes to column/FK-predicate
    suggestions (`:38-45`), so a qualifier that is a schema rather than an in-scope alias falls through to
    nothing. Disambiguate alias-vs-schema against `Schemas` + `sources`, and on a schema qualifier list
    that schema's tables. Related: `TableSuggestions` inserts a **bare** `{t.Name} {alias}`
    (`:106`) with the schema shown only as dimmed `DetailText`, so accepting a table outside search_path
    yields SQL that won't resolve — qualify the replacement when the schema isn't reachable unqualified
    (`ResolveTable(null, name)` answers that).
  - Seam: all engine-side work is pure and belongs in `Bearing.Sql` with tests next to
    `CompletionEngineTests.cs` / `JoinCompletionTests.cs` (§2.5); only the glyph/template half touches
    `Bearing.App`. §9.5 — re-run the existing completion tests when touching the antlr4-c3 path. *(live QA)*
- [ ] **P2** Show the SQL in the write-confirm dialog. When `RequireWriteConfirmation` trips (`ExecuteAsync`
  and the inline-edit `SaveChangesAsync` path), the confirm prompt should display the statements about to run,
  not just ask yes/no — so "am I about to nuke prod" is answerable from the dialog. `ConfirmWriteAsync`
  (`IDialogService`/`DialogService`), callers in `ExecutionViewModel`.
- [ ] **P2** Rework the edit **Preview SQL** flow — currently a separate `[Preview SQL]` button that pops an
  overlay, and it looks bad. The generated DML should show up **automatically as part of the save
  confirmation** instead of being a manual pre-step; drop the standalone button (or demote it) once the
  confirm dialog carries the SQL. Folds into the write-confirm item above. `ResultView.PreviewSql` →
  `MainWindow.ShowPendingScript` / `MainWindow.Overlays.cs`, `ResultView.Cells.cs:312`.
- [ ] **P2** **Export results to Excel** (+ CSV) with an "open containing folder" action after the export
  completes. Decide the xlsx route (a package vs. hand-rolled OOXML — note §0.1: nothing new in `Core`);
  export should offer loaded-rows vs. whole-result (needs the fetch-all item below). **The button already
  exists and is deliberately dead** — `ResultView.Cells.cs:304` renders `⭳ Export` with the tooltip
  "Export — coming soon" ("rendered; wired later (per decision)"), so this is wiring a present affordance,
  not adding one. Nothing else export-related exists in `src/`.
- [ ] **P2** **Fetch all rows** button on a paged result — one action that loops `LoadMore` to completion
  (cancelable, with progress/row count) instead of clicking through pages. Guard the obvious foot-gun on huge
  results. `ResultSetViewModel.IsPageable/HasMore/AppendPage`, `ExecutionViewModel.LoadMoreAsync` (`PageSize` 100).
- [ ] **P2** **Copy as…** — extend grid copy beyond TSV: HTML, Markdown, JSON, CSV, and SQL (`INSERT`
  statements / `VALUES` list). Context menu + palette commands; pure formatters under `Results/`
  (§2.5, testable without a grid) reusing the selection-rectangle logic in `ResultView.Selection.cs:209`.
- [ ] **P2** Checkbox (bool) cells don't take grid selection — clicking one toggles the value but leaves the
  cell/row selection where it was, so keyboard nav and copy act on the wrong cell. Make the bool cell set the
  selection on click like every other cell. `ResultView.Cells.cs` `BoolCell` (~line 170).
- [ ] **P2** `Tab` should move between rows/fields for view+edit in both table and pane (record) mode —
  a consistent forward/back field traversal (`Tab`/`Shift+Tab`) that commits the current cell and advances,
  wrapping to the next row at the end. Currently only spatial arrow nav exists in the grid.
- [ ] **P2** Configurable + dynamic font size (per tab). A configurable **base** font size (lives in the Settings
  framework above) applied to the editor. On top of that, dynamic zoom while a tab is open: `Ctrl+=`/`Ctrl+-`
  bump the *current* tab's font size up/down (and a `Ctrl+0`-style reset), **per tab** — each tab keeps its own
  zoom. The zoom is transient: reopening a tab (or a fresh tab) starts from the base size again, not the last
  zoom. Wire the zoom as commands in the input pipeline (`CommandIds` + `KeymapDefaults`, §9.2), not ad-hoc key
  handlers; store the current size on `EditorTabViewModel` (not persisted) and bind the editor `FontSize` to it.
  Decide whether results-grid font follows the same zoom or only the editor.

---

## 🔴 Open — correctness & security (from review)

- [x] **P1** `FileFallbackSecretStore` non-atomic write can corrupt/lose a stored password; `chmod 600` happens *after* a world-readable write (TOCTOU). `FileFallbackSecretStore.cs:25-31`. **Fixed:** write to `<file>.tmp`, `chmod 600` the temp, then atomic `File.Move(overwrite)` — a crash mid-write keeps the old secret, and the file is never world-readable. (+3 assertions)
- [ ] **P1** `CountAsync` swallows all errors as "uncountable" → paging hides totals on real DB failure. `PostgresQueryExecutor.cs:83-86` (`catch { return null; }`).
- [x] **P1** `StatementSplitter`: trailing `-- line comment` swallows the auto-appended `;` (merges statements → syntax error); blank-line heuristic mis-splits a single statement with a blank line at paren depth 0. `StatementSplitter.cs`. **Fixed:** `EnsureSeparated` puts the `;` on its own line after a fragment ending in a line comment; the blank-line split now fires only when the next token starts a statement (`StartsStatement`) and the previous token isn't a set operator (`EndsWithSetOperator`), so a statement continued by `and`/`order by`/`union` no longer mis-splits. (+4 tests)
- [x] **P1** `CellFormat.FormatArray` throws on multi-dimensional Postgres arrays (uses `arr.Length` with single-index `GetValue`). `Formatting/CellFormat.cs:36-41`. **Fixed:** `foreach` flattens any rank in row-major order instead of single-index `GetValue`. (+1 test)
- [ ] **P2** `EnsureSchemaAsync` inflight keyed by ConnectionId not (id, database) → wrong-DB snapshot across a rebuild. `ConnectionSessionManager.cs:155-157` (`_schemaInflight[session.ConnectionId]`). Note §9.4: sessions are keyed by connection Id by design, so the fix is the *inflight* key, not the session key.
- [ ] **P2** Missing `ConfigureAwait(false)` throughout the data layer (deadlock risk for any sync-over-async caller). Re-verified: **zero** occurrences in `PostgresQueryExecutor.cs`, `PostgresMetadataReader.cs`, `NpgsqlConnectionFactory.cs`.
- [ ] **P2** `ForeignKeyResolver` assumes equal-length parent/referenced attnum lists → `IndexOutOfRange` on a malformed composite FK. `Core/Schema/ForeignKeyResolver.cs:45-56`: the loop is bounded by `fk.ParentOrdinals.Count` (:48) but indexes `fk.ReferencedOrdinals[i]` (:50), so a shorter referenced list throws.
- [x] **P2** Write-guard gap: inline result-grid saves bypass the confirm dialog. **Already fixed** (landed in the merged review-fixes) — `ExecutionViewModel.SaveChangesAsync:245-254` confirms via `ConfirmWriteAsync` when the connection has `RequireWriteConfirmation`, mirroring the `ExecuteAsync` gate.
- [ ] **P3** `NpgsqlConnectionFactory` applies persisted options verbatim — unknown key throws unwrapped at connect; an `Options["Password"]` overrides the secret. `NpgsqlConnectionFactory.cs:26-40` (`default: csb[key] = value`). Note this also makes the *documented* `entra.resource` override unusable — setting it breaks the connection, because nothing filters app-level `entra.*` keys out before the rest go to `csb[key] = value`. Fixing the factory to ignore keys it doesn't own (or namespacing app options away from driver options) is the prerequisite for any future per-connection Entra option.
- [ ] **P3** Raw `ex.Message` surfaced to the UI on generic catch paths (host/endpoint info leak). `PostgresQueryExecutor.cs:45,69,119`.
- [ ] **P3** `SchemaBrowser.BuildAsync` catch removes the key unconditionally → can evict a concurrent replacement (pool leak). `SchemaBrowser.cs:80-86`. **Narrower than originally written:** not caching a failed build is deliberate and documented there (so the next expand can retry); only the concurrent-replacement race remains — remove the key *only if it still maps to this build's task*.
- [~] **P3** `JsonSessionStore` — **the atomicity half is fixed**: both `SaveAsync` (:26-29) and the shutdown-path `Save` (:39-41) now write `<file>.tmp` then `File.Move(overwrite: true)`. **Still open:** `LoadAsync` (:12-19) has no try/catch, so a truncated or hand-edited `session.json` throws on project open instead of falling back to defaults — inconsistent with `AppSettingsStore`.
- [ ] **P3** `secret-tool` delete ignores exit code → a failed clear leaves a stale credential after "delete". `SecretToolSecretStore.cs:35-36` (`DeleteAsync` discards the tuple, unlike `SetPasswordAsync` which throws on non-zero).
- [ ] **P3** `ResultSetViewModel.ToggleDelete` un-delete drops a prior pending edit. `ResultSetViewModel.cs:183-197`: marking a row deleted also does `_edited.Remove(row)` (":193 — delete supersedes pending edits"), and un-marking can't restore it — so the grid still shows the edited values but they'll never be saved.
- [ ] **P3** `ChangedAssignments` compares edited string vs typed original → emits no-op UPDATE assignments. `ResultEditModel.cs:140`: `Equals(row[i], original[i])` runs *before* `Coerce`, so a grid-written `"5"` never equals a typed `5`.
- [ ] **P3** `GestureParser` accepts numeric/undefined enum values (`Ctrl+16` binds `(Key)16`). `Input/GestureParser.cs:51,59` — partially mitigated (`key != Key.None` now guards the one worst case) but `Enum.TryParse` still accepts numeric tokens and there's no `Enum.IsDefined` check on either `Key` or `PhysicalKey`.
- [ ] **P3** MRU tab-cycle state hard-coupled to the Ctrl key — rebinding `tab.mruNext` freezes MRU ordering. Moved by the partial-class split: now `MainWindow.Commands.cs:214` (`e.Key is Key.LeftCtrl or Key.RightCtrl && _mruCycling`), flag declared at `MainWindow.axaml.cs:40`.
- [x] **P3** `StreamAsync` ignores `QueryOptions.MaxRows` — **closed by deleting the dead API.** No `StreamAsync` remains anywhere in the repo. (`QueryOptions.MaxRows` itself is live and honoured on the paging path: `PostgresQueryExecutor.cs:145`, set to `PageSize` by `ExecutionViewModel.cs:192,326`.)
- [ ] **P3** Recent-projects dropdown isn't pruned of missing/empty dirs (resume skips them, but the list still shows them). Moved with the VM decomposition: now `ShellViewModel.Projects.cs:120-125` (`RefreshRecentAsync` adds every entry `ListAsync` returns, with no existence filter).

---

## 🟡 Open — quality & maintainability

- [~] **P2** Decompose the god objects — **the VM third is done; the two views are not.**
  - **Done: `MainWindowViewModel` (1014) is gone.** Really decomposed, not just split: `ShellViewModel`
    (153 + `.Session` 59 + `.Projects` 158) now delegates to child VMs behind `WorkspaceContext` —
    `WorkspaceViewModel` 153, `ConnectionsViewModel` 423, `ExecutionViewModel` 422, `ScriptsViewModel` 149,
    `HistoryPanelViewModel` 130. This is the pattern the remaining two should follow.
  - [ ] **`Controls/ResultView`** — still **one** `sealed partial class` over 7 files, **~1,902 lines**:
    `.Cells` 431, `.Selection` 339, `.Layout` 322, `.Inspector` 265, `.Grid` 224, `.Rendering` 178, root 143.
    The JSON inspector is a self-contained overlay → its own control; cell factories and the
    selection-rectangle math are pure enough to move under `Results/` (§2.5).
  - [ ] **`Views/MainWindow`** — still **one** `partial class` over 6 files, **~1,053 lines**:
    `.Commands` 440, `.axaml.cs` 312, `.Chrome` 127, `.Palette` 112, `.ConnectionCommands` 36,
    `.Overlays` 26. The palette overlay and the editor text ops in `.Commands` are the separable parts
    (the latter overlaps the editor-shortcuts item above — do them together).
  - Note the split-into-partial-*files* move already happened for both and is what §9.1 warns against: the
    line count hid, the concerns didn't separate. Extract types, and leave thin delegating members so the
    binding surface is unchanged.
- [x] **P3** Remove dead `Views/HistoryWindow.axaml(.cs)` — **done**, the files are gone; the inline History panel is the only history UI.
- [~] **P3** The "coming soon" stubs. *(`About` done — `Views/AboutDialog.cs` shows name/tagline/version from `<Version>` in `Directory.Build.props`.)* **Two** remain, and both belong to other items rather than here: **Settings** (`MainWindow.Chrome.cs:107`, sets the status text "Settings — coming soon") → the Settings-framework item above; **Export** (`ResultView.Cells.cs:304`, a rendered-but-unwired `⭳ Export` button) → the export item above. Nothing to do in this entry except not forget the third stub exists.
- [~] **P3** Clear build warnings — **3 remain**, verified this pass; the obsolete `TextBox.Watermark` → `PlaceholderText` set is **fixed**. Left: `CS0108` `StatementMargin.Width` hides `Layoutable.Width` (`Editing/StatementMargin.cs:16`), and `xUnit2013` ×2 (`HistoryPanelTests.cs:51,53` — use `Assert.Single`).

---

## 🔵 Open — hardening backlog (pre-existing)

- [ ] **P1** Encrypt the fallback secret store and/or add platform keychains (DPAPI / macOS Keychain). Today: base64 fallback, libsecret on Linux only. (documented, warned in UI)
- [ ] **P2** Query-log privacy: file perms (0600), optional encryption, and/or PII/literal stripping. Retention exists (default 180d); no stripping. `SqliteQueryLog.cs`.
- [ ] **P2** Enforce/prompt TLS (`sslmode`) — currently only set if the user adds the option manually.
- [ ] **P3** Settings UI for query-log retention (file-edit only today) and the 30-min idle timeout (currently a fixed constant).
- [ ] **P3** CI pipeline (build + `dotnet test`). Previously skipped.
- [x] **P3** Deeper VM decomposition — **done for the VM half**: the stateful coordinators
  (connections / execution / tabs / panels) now live in `ConnectionsViewModel`, `ExecutionViewModel`,
  `WorkspaceViewModel`, `ScriptsViewModel` behind `WorkspaceContext`. The overlay-code-behind half is
  tracked in the god-object item above, not here.

---

## Notes

- GUI can't be driven headlessly here (Wayland) — items tagged *(live QA)* need a manual pass. Several
  **committed** features have never had one: the rename, credential sources, and the connection-status
  toggle. Committed ≠ eyeball-verified.
- Keep this file honest by re-checking file:line references when you touch an item. The 2026-08-06
  reconciliation found the two systematic drifts to watch for: **the partial-class split** moved a lot of
  code (`MainWindowViewModel` → `ShellViewModel` + child VMs, `MainWindow`/`ResultView` into partials), so
  pre-split line references are wrong; and **five items had quietly closed** (`StreamAsync`,
  `HistoryWindow`, the `Watermark` warnings, half of `JsonSessionStore`, the VM decomposition) while their
  entries still read as open.
- Cross-cutting overlaps worth batching rather than doing twice:
  - **Window-closing hook** — Stage E's quit-confirm needs it. The scratch save-prompt turned out **not**
    to: quitting already round-trips every buffer through `session.json`, so the prompt is tab-close only.
  - **Write-confirm dialog** — showing the SQL and reworking Preview SQL are one job.
  - **`ConnectionInfo.Options` handling** — the P3 factory item is the prerequisite for any app-level
    (`entra.*`) option key, since unknown keys currently reach Npgsql and throw.
  - **Settings framework** — the model/store exist; only the UI is missing. Five options want a home in it
    (query-log retention and autosave mode are live but file-edit-only; idle timeout, TLS preference, and
    base font size aren't settings yet).
  - **Editor text ops** — the `Ctrl+U`/`Ctrl+W` helpers landed as `Bearing.Sql/TextDeleter.cs`; carving up
    `MainWindow.Commands.cs` still touches the same code, and `EditorSpan`/`ApplyDelete` are the shape the
    rest of the editor ops should be pulled into.
- The `SELECT … INTO` write-guard case and the recent-project pruning were partially informed by, and
  partially close, review findings — cross-check before re-doing.
