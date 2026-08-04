# Squirrel roadmap

Single tracking list for outstanding work. Grouped by status, then priority (**P1** correctness/security ·
**P2** UX/robustness · **P3** cleanup). Check items off as they land. Sources: the 2026-07-20 whole-codebase
review, the earlier hardening review, and feature requests.

Legend: `[ ]` open · `[~]` in progress · `[x]` done. "(uncommitted)" = in the working tree on `main`, not yet committed.

---

## ✅ Done this session — uncommitted, needs commit + live QA

- [x] **Resume last-used project on startup** — `App.ResumeLastProjectAsync`; skips deleted/unreadable recent entries. (+2 tests)
- [x] **Server-side first-page `LIMIT`** — `Squirrel.Sql/FirstPageLimiter.cs`; remote SELECT fetches one page instead of streaming the whole set; edit/FK-nav preserved. (+23 tests, 10 live)
- [x] **Honest query timer** — status bar shows wall-clock (`DescribeResults(results, wallClock)`).
- [x] **WriteGuard extended** — CREATE/CTAS, COPY, CALL, DO, GRANT/REVOKE, REFRESH, top-level `SELECT … INTO`. (+15 tests)
- [x] **Connection lease + 30-min idle eviction** — `SessionLease` + `ConnectionSessionManager` rewrite; running query can't be disposed under; idle connections reclaimed. Fixes the disposal-mid-query race. (+4 tests)
- [x] **Global error handling** — `CrashLog`, `CrashReporter`, `Views/ErrorDialog`, `Dispatcher.UnhandledException` (kept alive) + AppDomain/TaskScheduler backstops + guarded command dispatch.

> Verified: Sql 119 · App 142 (10 live pagila) · Persistence 13, all green; clean build + boot.
> **Suggested:** commit this batch as one reviewable unit before starting Stage E.

---

## 🚧 Next feature — Stage E: background execution

Design: `docs/background-execution-plan.md`. Scope (confirmed): concurrent per-tab queries that survive
project switches + a completion toast (**no** background-jobs panel), per-tab cancel, quit-confirm.

- [ ] **P2** App-lifetime sessions — stop disposing `_sessions`/`_schemaBrowser` on project switch.
- [ ] **P2** Per-tab run state + concurrent `ExecuteAsync` (remove the global `IsBusy` gate).
- [ ] **P2** Completion routing — results to originating tab if open, else a toast (`WindowNotificationManager`). *(live QA)*
- [ ] **P2** Per-tab cancel (Esc / stop button) — repoint the existing cancel at the selected tab's CTS.
- [ ] **P2** Quit/tab-close confirm-then-cancel when a query is running. *(live QA)*

---

## 🟢 Open — features & UX requests

- [ ] **P2** Rename the product to **Bearing**. Cross-cutting: window titles, About dialog, menu labels
  and any user-facing "Squirrel" strings; brand assets (logo/mark, `.ico`/`.icns`/favicons, in-app mark,
  window icon); config/data dir names and any persisted paths (migrate or accept a reset); docs/README.
  Decide separately whether to also rename the `Squirrel.*` assemblies/namespaces and the repo (larger,
  code-only churn — can lag the user-facing rename).
- [ ] **P2** Settings screen + framework. Build a real settings window (the `Settings…` menu is still a
  "coming soon" stub) backed by a general settings framework: a typed settings model, load/save via
  `AppSettingsStore`, and a UI that groups options by category. First tenants already have homes elsewhere
  in this roadmap — query-log retention, the 30-min idle timeout, TLS/`sslmode` preference, restore-window-size —
  which should migrate into this screen rather than staying file-edit-only or hard-coded constants. Model the
  UI after the existing code-built `KeybindingsWindow` (Keyboard Shortcuts already lives under Edit ▸).
- [~] **P2** Manual connect / disconnect + connection status *(uncommitted, in live QA)*. Toolbar status dot + label (green Connected / amber Connecting / red Disconnected, semantic — never the environment color) mirrored in the status bar, plus a chain toggle that Connects / Cancels-connecting / Disconnects. Indicator reflects the real session pool via `IConnectionSessionManager.LiveChanged`, so a query-driven connect / idle eviction updates it too. No connect-on-tab-switch — connecting is explicit (Connect button) or on an action that needs it (Run, which now also loads the schema before building results so first-page edits work). `ConnectionState` + state machine in `ConnectionsViewModel`; reusable `Controls/ConnectionStatusView`. Remaining: live QA of layout + behavior.
- [ ] **P2** Remove / delete projects. Delete a project from the recent list, and optionally from disk (with confirm). Also prune stale/missing entries from the recent list (see the P3 recent-projects item below).
- [ ] **P3** Restore last window size on startup; persist size only and let the window manager handle placement/position. (Add to session or app-settings state; apply in `App`/`MainWindow`.)
- [ ] **P3** Selecting a script that's already open should focus its existing tab (not open a duplicate / no-op). `OpenScriptInNewTabAsync` already focuses an existing tab on open; verify the single-click/select path in the scripts tree does the same. `ShellViewModel.Scripts` / `SidebarView`.
- [ ] **P2** Keyboard shortcuts for result-grid editing — save changes / discard / add row. Delete-row and begin-edit already have grid commands (`grid.delete`, `grid.beginEdit`); add `grid.save` / `grid.discard` / `grid.addRow` to `CommandIds` + `KeymapDefaults`, register them in `ResultView`'s grid scope (`RegisterGridCommands`), gated on `IsEditable`/`HasPendingChanges`.
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
  export should offer loaded-rows vs. whole-result (needs the fetch-all item below). No export exists today.
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
- [ ] **P1** `CountAsync` swallows all errors as "uncountable" → paging hides totals on real DB failure. `PostgresQueryExecutor.cs:84`.
- [x] **P1** `StatementSplitter`: trailing `-- line comment` swallows the auto-appended `;` (merges statements → syntax error); blank-line heuristic mis-splits a single statement with a blank line at paren depth 0. `StatementSplitter.cs`. **Fixed:** `EnsureSeparated` puts the `;` on its own line after a fragment ending in a line comment; the blank-line split now fires only when the next token starts a statement (`StartsStatement`) and the previous token isn't a set operator (`EndsWithSetOperator`), so a statement continued by `and`/`order by`/`union` no longer mis-splits. (+4 tests)
- [x] **P1** `CellFormat.FormatArray` throws on multi-dimensional Postgres arrays (uses `arr.Length` with single-index `GetValue`). `Formatting/CellFormat.cs:36-41`. **Fixed:** `foreach` flattens any rank in row-major order instead of single-index `GetValue`. (+1 test)
- [ ] **P2** `EnsureSchemaAsync` inflight keyed by ConnectionId not (id, database) → wrong-DB snapshot across a rebuild. `ConnectionSessionManager.cs`.
- [ ] **P2** Missing `ConfigureAwait(false)` throughout the data layer (deadlock risk for any sync-over-async caller). `PostgresQueryExecutor.cs`, `PostgresMetadataReader.cs`, `NpgsqlConnectionFactory.cs`.
- [ ] **P2** `ForeignKeyResolver` assumes equal-length parent/referenced attnum lists → `IndexOutOfRange` on a malformed composite FK. `Core/Schema/ForeignKeyResolver.cs:45-52`.
- [x] **P2** Write-guard gap: inline result-grid saves bypass the confirm dialog. **Already fixed** (landed in the merged review-fixes) — `ExecutionViewModel.SaveChangesAsync:245-254` confirms via `ConfirmWriteAsync` when the connection has `RequireWriteConfirmation`, mirroring the `ExecuteAsync` gate.
- [ ] **P3** `NpgsqlConnectionFactory` applies persisted options verbatim — unknown key throws unwrapped at connect; an `Options["Password"]` overrides the secret. `NpgsqlConnectionFactory.cs:37`.
- [ ] **P3** Raw `ex.Message` surfaced to the UI on generic catch paths (host/endpoint info leak). `PostgresQueryExecutor.cs:45,70,120`.
- [ ] **P3** `SchemaBrowser.BuildAsync` catch removes the key unconditionally → can evict a concurrent replacement (pool leak). `SchemaBrowser.cs:85-90`.
- [ ] **P3** `JsonSessionStore.Save` non-atomic + `LoadAsync` has no try/catch (a crash during shutdown-save bricks the next open; inconsistent with `AppSettingsStore`). `JsonSessionStore.cs`.
- [ ] **P3** `secret-tool` delete ignores exit code → a failed clear leaves a stale credential after "delete". `SecretToolSecretStore.cs:35`.
- [ ] **P3** `ResultSetViewModel.ToggleDelete` un-delete drops a prior pending edit. `ResultSetViewModel.cs:181`.
- [ ] **P3** `ChangedAssignments` compares edited string vs typed original → emits no-op UPDATE assignments. `ResultEditModel.cs:140`.
- [ ] **P3** `GestureParser` accepts numeric/undefined enum values (`Ctrl+16` binds `(Key)16`). `Input/GestureParser.cs:51,59`.
- [ ] **P3** MRU tab-cycle state hard-coupled to the Ctrl key — rebinding `tab.mruNext` freezes MRU ordering. `MainWindow.axaml.cs:979`.
- [ ] **P3** `StreamAsync` ignores `QueryOptions.MaxRows` — but has **no production caller**; decide: implement the cap or delete the dead API. `PostgresQueryExecutor.cs:158`.
- [ ] **P3** Recent-projects dropdown isn't pruned of missing/empty dirs (resume skips them, but the list still shows them). `MainWindowViewModel.RefreshRecentAsync`.

---

## 🟡 Open — quality & maintainability

- [ ] **P2** Decompose the god objects: `Controls/ResultView.cs` (1716), `Views/MainWindow.axaml.cs` (1615), `MainWindowViewModel.cs` (1014). Overlay builders + tree-search are the most separable.
- [ ] **P3** Remove dead `Views/HistoryWindow.axaml(.cs)` (replaced by the inline History panel).
- [~] **P3** Implement or hide the `Settings…` / `About` menu stubs ("coming soon"). *(About done — `Views/AboutDialog.cs` shows name/tagline/version; version comes from `<Version>` in `Directory.Build.props`. Settings still a stub.)* *(live QA)*
- [ ] **P3** Clear build warnings: `CS0108` `StatementMargin.Width` hides `Layoutable.Width`; obsolete `TextBox.Watermark` → `PlaceholderText` (×3); `xUnit2013` in `HistoryPanelTests` (×2).

---

## 🔵 Open — hardening backlog (pre-existing)

- [ ] **P1** Encrypt the fallback secret store and/or add platform keychains (DPAPI / macOS Keychain). Today: base64 fallback, libsecret on Linux only. (documented, warned in UI)
- [ ] **P2** Query-log privacy: file perms (0600), optional encryption, and/or PII/literal stripping. Retention exists (default 180d); no stripping. `SqliteQueryLog.cs`.
- [ ] **P2** Enforce/prompt TLS (`sslmode`) — currently only set if the user adds the option manually.
- [ ] **P3** Settings UI for query-log retention (file-edit only today) and the 30-min idle timeout (currently a fixed constant).
- [ ] **P3** CI pipeline (build + `dotnet test`). Previously skipped.
- [ ] **P3** Deeper VM decomposition: stateful coordinators (connections/execution/tabs/panels) out of `MainWindowViewModel`; overlay code-behind out of `MainWindow.axaml.cs`.

---

## Notes

- GUI can't be driven headlessly here (Wayland) — items tagged *(live QA)* need a manual pass.
- Nothing is committed yet. The "Done this session" batch is a clean unit to commit first.
- The `SELECT … INTO` write-guard case and the recent-project pruning were partially informed by, and
  partially close, review findings — cross-check before re-doing.
