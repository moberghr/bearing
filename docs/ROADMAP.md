# Bearing roadmap

Single tracking list for outstanding work. Grouped by area, then priority (**P1** correctness/security ·
**P2** UX/robustness · **P3** cleanup). Sources: the 2026-07-20 whole-codebase review, the earlier hardening
review, and feature requests.

Legend: `[ ]` open · `[~]` partly done.

> **2026-08-12: completed items were removed from this file.** It used to carry a full "how it landed and why
> it diverged" record of every finished item; that history is in `git log` (and in the file's own history) and
> the code, and the list had become mostly archive. **What remains below is only what is open or partly done**,
> plus seven items added this pass (tab context menu, pinned menu bar, row/column header selection, unpaged
> fetch-all, Windows keyring, Velopack distribution, clipped test-connection error). The 🔴 *correctness &
> security* section is gone because it was empty — every review finding is closed.
>
> **2026-08-13:** four more requests added under *Tabs & shell chrome* — scratch label vs. date-stamped file
> name, tab-title tooltip, "reveal in Scripts" / "open containing folder" (folded into the existing tab
> context-menu item), and the off-centre rail icons. The first three all lean on the same missing link between
> a tab and its file on disk; read them together.
>
> **2026-08-13 (later):** two items **done and removed** — the "Show \<X\> panel does nothing when collapsed"
> bug (now an explicit `ShellViewModel.ShowPanel`, six call sites, not the five the entry predicted — the
> toolbar History button had it too; covered by `tests/Bearing.App.Tests/SidePanelRevealTests.cs`) and the
> clipped connection-dialog Test error (wrapping + selectable + `SafeErrorText`). Both still want *(live QA)*.
>
> **2026-08-13 (secret-store probe):** the app claimed *"No system keyring found"* on a Linux box whose
> libsecret worked perfectly. Two causes, both now fixed: the probe ran **once** at startup
> (`App.axaml.cs`), so a keyring that wasn't serving at that instant pinned the session into the file
> fallback until restart — there is now an upgrade-only `ShellViewModel.RefreshSecretStorageAsync`, re-asked
> before the connection dialog opens; and `ProbeAsync` caught every failure into a bare `false`, discarding a
> message that already held `secret-tool`'s own stderr — it now returns a redacted reason which
> `CreateAsync` writes to `crash.log` via the new `CrashLog.Note`. 14 new tests
> (`SecretStoreProbeTests`, `SecretStorageRefreshTests`). **Unverified against the original symptom**: it
> needs the transient to recur, so the log line is the evidence to look for next time.
>
> **2026-08-13 (schema cache):** *completion dying on disconnect* is **done and removed**. Snapshots now live
> in a `(connection id, database)` cache on `ConnectionSessionManager` that **outlives sessions**
> (`TryGetSnapshot`), because a snapshot never needed the connection — only the catalog it already read.
> `SnapshotForSelectedTab` reads it, and only after checking that a live session is on *this* database (an
> id-only match would have let database B serve database A's catalog, which drives editability and FK nav).
> The staleness call: **kept** across disconnect, idle sweep, credential expiry and project switch; **dropped**
> only by the three events that make the catalog untrue — connection re-pointed at another server, deleted, or
> explicitly refreshed — via the new `InvalidateSchema`. Bonus payoff: a reconnect adopts the cached snapshot
> instead of re-reading the catalog. 10 new tests (`SchemaCacheTests`).
> **Found and fixed on the way:** `EnsureSchemaAsync` registered its single-flight entry *after* starting the
> load, while `LoadSchemaAsync` clears that key in its own `finally` — so a load completing **synchronously**
> left a completed task in `_schemaInflight` that nothing ever cleared, and every later call for that key was
> served the stale snapshot instead of re-reading. Latent in production only because real Npgsql reads always
> yield; it would have broken the schema-refresh path the moment anything cached.
>
> **2026-08-13 (later still):** three requests added — completion going dead on disconnect and better
> autocomplete matching (the first is now done — see above), and a multi-sheet Excel workbook per run (end of
> *Results grid*). Two things to know before reading them: **Excel export already exists** and is a typed hand-rolled workbook, so that item is only
> "many sheets + naming"; and the fuzzy-match item is **not** an engine change — the engine emits every table
> unfiltered and AvaloniaEdit does all the narrowing today, which is what has to be taken over.
>
> **2026-08-13 (test server defaults):** a bare `dotnet test` used to skip all 26 Postgres tests. The default
> port was **5433** in the six `Bearing.Data.Tests` files and **5434** in the two `App` ones — six copies of the
> same `Env`/`Reachable` pair that had drifted — and on this box 5433 is a *different* project's Postgres, so
> the probe got `28P01 password authentication failed` and `catch { return false; }` reported it as "No
> PostgreSQL reachable". Now one linked `tests/Shared/PgTestServer.cs` holds the defaults (5434) and its
> `RequireAsync` puts the endpoint **and the driver's reason** in the skip message. Same fix in
> `PlatformKeychainTests.RequireStoreAsync`, which was calling the reason-discarding
> `CreatePlatformStoreAsync` instead of `ProbePlatformStoreAsync` — on Windows/macOS "no credential store
> here" is never true, so a silent skip there was hiding the very failure that suite exists to catch.
>
> Baseline at the last verified pass (2026-08-13): build clean with **4 warnings** — the 2 known `xUnit2013`
> plus 2 `ANT01` from the vendored PostgreSQL lexer grammar, which the previous "2 warnings" count omitted;
> tests **Sql 163 · App 466 · Data 27 · Persistence 46** (702) — **all 702 passed with 0 skipped** on a bare
> `dotnet test` against the `squirrel-pg-test` container, no env vars. A Postgres or keychain skip now carries
> its own diagnosis, so read the message before assuming the server is down. (`--no-incremental` for the
> warning count — an incremental build re-emits none, which is easy to misread as "fixed".)
>
> **If a build fails with `MSB1025` / `SocketException (13)` or "Access to the path `.gitmodules` is denied",
> that is an agent sandbox, not the repo:** add `-m:1 -nodeReuse:false` (MSBuild's worker socket) and
> `-p:EnableSourceControlManagerQueries=false` (`.gitmodules` masked with `/dev/null`). Note the
> `SkippableFact` suites go **quiet** under those conditions — a green run with skips is not evidence the
> keychain or Postgres paths work.
>
> **Nothing visual is ever verified here** (§4.3 — Wayland blocks headless GUI testing). Tag new UI work
> *(live QA)* until the user has eyeballed it.

---

## 🟢 Open — features & UX

### Tabs & shell chrome

- [ ] **P2** **A scratch tab's label and its backing file name have nothing to do with each other.** The tab
  says `Scratch 3`; the file in the scripts tree is `2026-08-13-02.sql`, and **nothing on screen connects the
  two**. The label is `$"Scratch {++_scratchCounter}"` (`WorkspaceViewModel.cs:89`, counter at `:32`) and
  `Header` deliberately hides the filename for scratch tabs (`EditorTabViewModel.cs:196` —
  `IsScratch || ScriptPath is null ? DisplayName : Path.GetFileName(ScriptPath)`, with a comment saying the
  hiding is on purpose); the filename is `{yyyy-MM-dd}-{nn}.sql` from `ScratchNaming.NextFileName`
  (`ScratchNaming.cs:23-32`) under `<project>/scripts/scratch` (`ProjectModels.cs:31`), created **lazily on
  first non-blank content** (`TabAutosave.cs:154,167`). Unify them — and note the ordinals can't be made to
  agree by accident, because they count different things: the label counter is per app-session and never
  resets per day, the file ordinal restarts daily and fills gaps.
  - **Recommended direction: make the file name the single source of truth and drop the special case**, so
    the tab reads `2026-08-13-02.sql` once the file exists and keeps `Scratch N` only as the pre-file
    placeholder. That is what the scripts tree, the hover tooltip and "reveal in Scripts" (both items below)
    all show, so it's the name the user can actually act on. The alternative — name the file after the label
    (`scratch-3.sql`) — reads nicer but throws away the daily bucketing and gives cross-day collisions to
    resolve; if you go that way, do it in `ScratchNaming` and keep the date somewhere.
  - **`_scratchCounter` increments on every `NewTab`, including opening a named script** (`:89` is on the
    shared path), so the numbering already has gaps today regardless of which direction you pick — worth
    fixing in the same pass.
  - Session restore carries the label separately as `OpenEditor.ScratchName` (`ProjectModels.cs:44`, written
    `ShellViewModel.Session.cs:70`, re-applied `WorkspaceViewModel.cs:201,210`) and resets the counter to 0
    (`:184`). If the label becomes derived, that field is redundant — decide whether to keep reading it for
    backward compatibility with existing `session.json` files or to drop it.
  - Don't disturb **promotion**: `RenameTabAsync` (`WorkspaceViewModel.cs:278-296`) flushes, sets
    `DisplayName`, moves the file out of `scripts/scratch` and re-derives `IsScratch` from *location*
    (`ScratchNaming.IsUnderScratch`) — a renamed scratch must still end up showing its new file name, and an
    *empty* scratch (no file yet) must still keep the typed label (`:291`). `ScratchNaming` is pure and
    already has a home for tests (§2.5).
- [ ] **P2** **Hovering a tab title should show the full backing file name.** There is no `ToolTip.Tip`
  anywhere on the tab header — the item template's root `StackPanel` (`MainWindow.axaml:367`) and the title
  `TextBlock` (`:368`, `Text="{Binding Header}"`) both lack one; the only tooltips in the template are on the
  spinner (`:373`), the dirty dot (`:388`) and the close button (`:396`). Bind it to
  `EditorTabViewModel.ScriptPath` (`EditorTabViewModel.cs:41-43` — the absolute path, and the only path
  property; there is no `FilePath`). Two cases to handle deliberately: `ScriptPath` is **null** until a
  scratch tab's file is created, and a null tooltip shows nothing at all — either suppress the tooltip or say
  "not saved yet"; and the useful string is arguably the **project-relative** path rather than the absolute
  one (`ProjectDirectory` is on the VM at `:33`), since the absolute path is mostly `~/…/project/scripts/`
  noise. A tiny derived property on the VM keeps the formatting out of XAML and under test. This is the
  cheapest half of the scratch-naming item above — it makes the label↔file link visible even before the
  names are unified.
- [ ] **P2** **Right-click a tab title → Rename, Save, Close.** All three operations already exist per-tab and
  are reachable other ways; this is a discoverability gap, not new behaviour. Rename is `CommandIds.TabRename`
  (`MainWindow.Commands.cs:34`) plus the double-tap handler `OnTabHeaderDoubleTapped`
  (`MainWindow.axaml:367` → `MainWindow.Chrome.cs:23` → `WorkspaceViewModel.RenameTabAsync:278`, which is a
  file rename for a named script and a scratch **promotion** otherwise); Save is
  `WorkspaceViewModel.SaveScriptAsync(tab, path, text)`; Close is `CloseTabAsync` with its unsaved-work prompt.
  So the work is a `ContextFlyout` on the `TabStrip.ItemTemplate` root, bound to *that* tab's
  `EditorTabViewModel` rather than the selected one — the template's `DataContext` already is the tab, which
  is how the ✕ button and the double-tap rename get the right one.
  - **Flush the live editor first**, as `CloseTabAsync`'s callers do: the editor's document is the source of
    truth for the focused tab, so a Save driven from a *background* tab's menu must not write the focused
    tab's buffer.
  - Gate Save on there being something to save, and mirror the gestures in the menu (Rename, `Ctrl+F4` for
    Close) so the shortcuts stay discoverable. Pattern to copy: `Controls/ResultContextMenu` (the grid's
    right-click menu), which builds its items from the command table.
  - **Also: "Reveal in Scripts panel"** — select and scroll to this tab's file in the scripts tree, expanding
    its ancestors. Showing the panel is solved (`ShellViewModel.ActivePanel` / `SidePanel.Scripts`,
    `ShellViewModel.cs:123-128`; precedent `RevealTabAsync:160-169`) but **selecting a node by path is not
    built**: `ScriptsViewModel` has no `SelectedScript` / reveal member, and the tree at
    `SidebarView.axaml:88` binds only `ItemsSource` — no `SelectedItem` (contrast the History `ListBox` at
    `:189`, which does). Code-behind only ever *reads* `ScriptsTree.SelectedItem` (`SidebarView.axaml.cs:289`);
    it sets `SchemaTree.SelectedItem` (`:122,145`), which is the pattern to copy. `IsExpanded` is two-way
    bound per node (`SidebarView.axaml:93`), so expanding ancestors from the VM works — the path→node walk
    over `ScriptNodes` (`ScriptsViewModel.cs:38`, `ScriptItem.cs:15-37`) is pure and testable (§2.5). Watch
    `RefreshScripts` (`:45-65`), which rebuilds the node tree **wholesale** — a reveal has to survive, or
    re-run after, a refresh. This is also the payoff for the scratch-naming item above: revealing is how you
    answer "which file *is* this tab?" when the label doesn't say.
  - **And "Open containing folder"** — `Services/FileReveal.OpenContainingFolder(path)` already exists and
    already does the platform dispatch (`FileReveal.cs:17-39`: `explorer /select`, `open -R`, `xdg-open` on
    the folder). It takes a *file* path and derives the directory (`:21-22`), so pass `ScriptPath` straight
    in. Today it has exactly one call site — the export toast (`MainWindow.axaml.cs:229`). Both new items must
    be **hidden or disabled when `ScriptPath` is null** (an empty scratch has no file yet), and note
    `OpenContainingFolder` swallows failures and returns `false` (`:35-38`) — surface that in the status bar
    rather than dropping it silently.
- [ ] **P2** **Option to keep the menu bar visible.** The menu is Alt-tap-to-reveal only
  (`IsMenuVisible`, `MainWindow.axaml:113`, toggled at `MainWindow.Commands.cs:135`), which is invisible to
  anyone who doesn't know the gesture. Add a **pinned** mode: `AppSettings.ShowMenuBar` + one
  `SettingsCatalog` descriptor (General) — the framework then renders, searches, persists and resets it for
  free, and `WorkspaceContext.Settings` is a live property so no subscription is needed.
  - **The substance is the four auto-hide paths, which must all no-op when pinned**, or the menu will vanish
    the moment it is used: Escape-hides (`MainWindow.Commands.cs:163`), click-outside-hides (`:172`),
    hide-after-a-leaf-`MenuItem`-click (`:179`), and the Alt toggle itself (`:135`) — pinned, Alt should focus
    the menu, not hide it. Also `:44`/`:154`, which treat a visible menu as "a modal-ish surface is open" and
    suppress global keys; that is right for a transient reveal and **wrong** for a pinned bar, which must not
    swallow every shortcut in the app.
  - Cleanest shape is to keep `IsMenuVisible` as "is it on screen" and add a separate "is it pinned" read from
    settings, with every hide path asking the latter first.
- [ ] **P3** **Rail icons aren't optically aligned — the Scripts glyph is visibly off-centre** (reported
  2026-08-13 with a screenshot: the document sits left of its tile's centre while the database and clock above
  and below it look centred). Cause is `Stretch="Uniform"` + `Width/Height=20` on the shared rail-icon style
  (`MainWindow.axaml:31-39`): Avalonia fits the **geometry's own bounding box**, not the 24×24 design viewport
  the icons were drawn in (`Themes/Icons.axaml` header comment), so each icon gets its own scale and its own
  offset inside the 20×20 box. Measure the `Icons.axaml` data and it falls out: `Icon.History` (`:26`) is
  exactly 16×16 and centred, which is why it looks right; `Icon.Scripts` (`:17`) is 12×18 — the narrowest in
  the set, so it scales to ~13pt wide in a 20pt box and is the most obviously adrift. `Icon.Database` (`:45`,
  14×18) and `Icon.Connections` / `Icon.Schema` are off by smaller amounts, vertically as well as
  horizontally. So this is one style-level bug, not a bad path — fixing only the Scripts geometry would just
  move the inconsistency around.
  - Fix by pinning the viewport instead of the glyph bounds: host each `Path` in a fixed 24×24 `Canvas` inside
    a `Viewbox` (the **in-repo precedent** is the connection toggle, `MainWindow.axaml:41-46`, whose comment
    notes the `Viewbox` scales stroke width along with everything else — so `StrokeThickness` would then be in
    24-unit space and needs re-tuning from the current device-space `1.6`). Applies to the rail *and* the
    `Button.rail` reuse in `SidebarView` (`SidebarView.axaml:78`), since they share the selector.
  - Cheap alternative if the `Viewbox` stroke rescaling isn't wanted: keep `Stretch="Uniform"` and normalise
    every geometry to identical 24×24 extents by appending an invisible bounding subpath — mechanical, but it
    puts an easily-lost invariant in the data rather than in the layout, so prefer the `Viewbox`.
  - *(live QA)* — this is purely optical (§4.3), so the user's eye is the only check that it landed.

### Results grid

- [ ] **P2** **Click a row header to select the whole row; click a column header to select the whole column.**
  The grid already shows both headers (`HeadersVisibility = DataGridHeadersVisibility.All`,
  `ResultView.Grid.cs:43` — a row-number gutter plus column headers, both styled in `ResultGridChrome:74,82`)
  but neither is clickable, so there is no way to grab a row or a column short of dragging across it. The
  primitive exists: `GridSelectionController.SelectRectangle(result, a, b)` takes two corners, so a row is
  `(row, 0)`→`(row, lastCol)` and a column is `(firstRow, col)`→`(lastRow, col)`.
  - **The column-header conflict is the trap**: a double-tap on a column header (or its resize gripper)
    already means *auto-fit the column* (`ResultGridChrome.AutoFitColumn:94`, wired from
    `ResultView.Grid.cs`). Single-tap-selects plus double-tap-autofits on the same control means the first tap
    of a double-tap will also select — acceptable (the selection is harmless and gets replaced), but decide it
    deliberately rather than discovering it.
  - **Decide what a column selection means on a paged result:** the loaded rows only, or the whole result. The
    honest options are to select what is loaded and say so in the meta row, or to fetch all first (the export
    path already answers this question the second way — see the unpaged-fetch item below). Selecting a column
    is the natural way to build a `Copy as ▸ SQL IN list`, and a silently-partial list is exactly the failure
    that item's NULL-dropping note is about.
  - Skip the measure column and bool/checkbox columns as `GridSelectionOps` already does; extend with
    Shift (contiguous rows/columns) and Ctrl (add to selection) to match cell behaviour. The
    row/column→rectangle mapping is pure and belongs beside `Results/GridSelectionOps` with tests (§2.5).
- [ ] **P2** **"Fetch all rows" should fetch unpaged, not walk the pages.** `ExecutionViewModel.FetchAllAsync`
  (`:349-384`) loops `ExecutePageAsync(PageSql.Page(rs.SourceSql, rs.Loaded, PageSize))` until `HasMore` goes
  false — so fetching 200k rows is 2,000 round-trips, each one **re-executing the whole query** with a growing
  `OFFSET` (`PageSql.Page:14` → a top-level `LIMIT/OFFSET` suffix, or a derived-table wrap). Two costs:
  - **Quadratic server work.** Postgres must produce and discard `OFFSET` rows every time, so the last page
    scans the entire result to throw almost all of it away.
  - **It isn't a consistent snapshot** — the real correctness argument. Each page is its own statement in its
    own implicit transaction, so a concurrent insert or delete shifts rows between pages: the fetch can
    duplicate rows or skip them, and then reports "Fetched all N rows" with `TotalCount = Loaded`. An export
    taken from that is wrong in a way nothing on screen reveals.
  - Fix: one unpaged execution of `rs.SourceSql`, streamed. Needs an executor entry point that doesn't cap at
    `PageSize` — `QueryOptions.MaxRows` is honoured on the paging path (`PostgresQueryExecutor.cs:145`, set
    from `ExecutionViewModel.cs:192,326`), so the ceiling becomes `MaxRows = ResultFetchAllMaxRows + 1`, where
    the `+1` is what makes "there was more" detectable — the current loop learns it from `HasMore`.
  - Keep everything the loop got right: the per-tab run lifecycle and Esc cancel (`RunExclusiveAsync`), one
    `SessionLease` for the whole read, live row-count progress (now driven by the reader rather than by pages),
    the **non-silent** stop at the cap (`:364-371`), and `TotalCount = Loaded` on completion so `[Count]`
    retires without a second query. Returning "did it complete" is what lets Export refuse to write half a
    result — don't lose it.
  - `rs` currently grows via `AppendPage`; an unpaged fetch either replaces the rows wholesale (simpler, but
    the grid loses scroll position and any pending edits) or appends in batches as the reader drains
    (keeps both, and keeps progress meaningful). Prefer the second. Cancel must keep the rows already
    materialized, as today. The `ConfigureAwait` pass in `Bearing.Data` was done *for* this path
    (`ReadResultSetAsync` awaits once per row) — a 200k-row read on the UI thread would freeze the window.
  - The existing 4 `FetchAllAndExportTests` cases (pages to the end in order, no-op when complete, stops at the
    ceiling, cancel keeps loaded rows) are the contract to hold; the ordering one changes shape.
- [ ] **P2** **Paste into the results grid (`Ctrl+V`)** — copy is done, paste doesn't exist. Requested
  2026-08-11: copy the selected cells' values, then select one or more cells and paste, editing those rows.
  Copy already works (`grid.copy`, `Ctrl+C`/`Ctrl+Insert` → `GridSelectionOps.Tsv`, plus Copy as ▸); there is
  **no** `grid.paste` in `CommandIds` and nothing in `src/` reads the clipboard, so this is all new. It now
  has a home in the UI: the grid's `Controls/ResultContextMenu`, next to Copy.
  - **Fill semantics** (the requested case): a single clipboard value pasted over an N-cell selection writes
    that value into every selected cell. A multi-cell TSV block anchors at the active cell and fills
    right/down — **decide** whether it clips to the selection or extends past it (Excel extends from a
    single-cell selection, clips/tiles when the selection is larger).
  - **Route it through `ResultSetViewModel.SetCell(row, col, string)`** — the exact call the in-cell TextBox
    editor makes (`ResultView.Grid.cs` `WireEditing` → `CellEditEnding`). Paste then inherits the whole
    existing edit path for free: the `(null)` token ⇒ NULL, empty ⇒ `""` for text / NULL otherwise, and
    per-column type coercion at save time (`ResultEditModel.Coerce`), plus the one transactional
    `ExecuteWriteAsync` batch and Discard. Do **not** add a second value-parsing path.
  - **Gate on `IsEditable` in `canRun`**, like `grid.delete` / `grid.beginEdit`. This is a real trap:
    `SetCell` writes `row[column]` *before* calling `MarkEdited`, and `MarkEdited` is the part that no-ops
    on a read-only result — so an ungated paste silently corrupts the displayed rows of a locked result
    without marking anything pending.
  - **Async/dispatch wrinkle:** the clipboard read is `IClipboard.GetTextAsync`, but grid commands are
    registered `KeyCommand.Sync` and `GridTarget()` reads `_keyStrokeTarget`, which `OnGridKey` clears in its
    `finally` the moment the dispatch returns. Capture the `(grid, result)` target **before** the first
    `await`, or the continuation silently falls back to the selection-owner lookup.
  - Repaint after writing (`ResultRowPainter.RefreshRowColors`) — otherwise pasted rows show no amber pending
    tint until they happen to re-realize. Bool/checkbox columns can't be paste targets (selection skips them,
    see the item below). Bind `Ctrl+V` + `Shift+Insert` to mirror the copy pair.
  - Pure part → parsing clipboard TSV into a rectangle and mapping it onto the selection belongs beside
    `Results/GridSelectionOps` (§2.5), unit-testable without a grid — the paste *shape* rules are exactly the
    kind of thing Wayland stops us verifying by hand (§4.3).
- [ ] **P2** Checkbox (bool) cells don't take grid selection — clicking one toggles the value but leaves the
  cell/row selection where it was, so keyboard nav and copy act on the wrong cell. Make the bool cell set the
  selection on click like every other cell. `Controls/ResultCellFactory.cs` `BoolCell`.
- [ ] **P2** `Tab` should move between rows/fields for view+edit in both table and pane (record) mode —
  a consistent forward/back field traversal (`Tab`/`Shift+Tab`) that commits the current cell and advances,
  wrapping to the next row at the end. Currently only spatial arrow nav exists in the grid.
- [ ] **P2** Keyboard shortcuts for result-grid editing — save changes / discard / add row. Delete-row and
  begin-edit already have grid commands (`grid.delete`, `grid.beginEdit`); add `grid.save` / `grid.discard` /
  `grid.addRow` to `CommandIds` + `KeymapDefaults`, register them in `ResultView`'s grid scope
  (`RegisterGridCommands`), gated on `IsEditable`/`HasPendingChanges`.
- [ ] **P2** **Export a whole run to one Excel workbook — one sheet per result set, with nameable sheets.**
  Requested 2026-08-13, explicitly as *"something to think about first"*, so this entry is a design brief
  rather than a work order.
  - **Excel export already exists**, and as a real typed workbook rather than CSV in disguise:
    `ExportFormat.Xlsx` (`ResultExport.cs:11-15`) writing through the hand-rolled `XlsxWriter` — bold frozen
    header, typed numbers and bools, dates as serials with a `numFmt`, `timestamptz` deliberately kept as ISO
    text. **Read that class's comment before reaching for ClosedXML/NPOI/OpenXml**: the no-library choice is
    argued there, and it also names the independent check for changes
    (`soffice --headless --convert-to csv`, since the unit tests only assert what the writer itself believes).
    Sheet naming exists too — `ResultExport.SheetName` uses the source table when known, else "Result"
    (`:82`), via `XlsxWriter.SafeSheetName` (`:195` — ≤31 chars, strips `[]:*?/\`, never blank). **So the two
    missing halves are: more than one sheet, and letting the user name them.**
  - **One sheet is baked into four places**, each trivially parameterizable but all of which must agree: the
    `sheet1.xml` `<Override>` in `[Content_Types].xml` (`:213`), the lone `<sheet … sheetId="1" r:id="rId1"/>`
    in `Workbook` (`:236`), the lone `rId1` relationship in `WorkbookRels` (`:228`), and the hardcoded entry
    path in `WriteSheet` (`:66`). Add a multi-sheet overload and keep today's single-sheet call as a wrapper
    over it.
  - **Trap: sheet names must be unique, and `SafeSheetName` can *create* collisions** by truncating two long
    names to the same 31 characters — and two result sets from the same table (an entirely normal run) collide
    even before truncation. Excel refuses a workbook with duplicate sheet names, so dedupe **after**
    sanitizing (`orders`, `orders (2)`, …), as a pure tested function.
  - **The real question is scope, and it's genuinely new.** All three export entry points are anchored to a
    *single* result set — the grid command (`ResultView.cs:148-153`), the right-click menu
    (`ResultContextMenu.cs:58-62`) and the meta-row ⭳ button (`ResultExportButton.cs:18-29`) — all funnelling
    through `Func<ResultSetViewModel, ExportFormat, Task>` (`MainWindow.axaml.cs:137` →
    `ExecutionViewModel.ExportAsync:418`). A workbook of *all* of a run's sets is per-**tab**
    (`EditorTabViewModel.Results`, `:74,83-107`) and so needs a new home — Query menu, palette command, or the
    results dock chrome — plus a gate to include only sets that have a grid (`rs.HasGrid`: a `DELETE`'s
    row-count result is not a sheet).
  - **It also multiplies the fetch-all problem.** A single export fetches to the end first and **abandons**
    rather than write a truncated result (`ExportAsync:425-429`). For N sets that is N fetches, each taking a
    lease and serialized by the per-tab `RunExclusiveAsync` — so decide up front whether the workbook is
    all-or-nothing, or writes the sheets that completed and says which it dropped. **Do the unpaged fetch-all
    item above first:** N page-walks over a live server is exactly where its consistency argument bites.
  - Naming UI: a pre-export dialog listing the sets with editable names (defaulting to `SheetName(rs)`) is the
    obvious shape, and the natural place to show which sets will be skipped and to let the user drop one. That
    would make it the first export path with a dialog of its own — *(live QA)*.

### Editor

- [ ] **P2** **Autocomplete matching is too literal — `accounting_lines` should be reachable from `al`, and
  from `accli` too.** Requested 2026-08-13: match at word starts, and support initials plus subsequences
  across `snake_case`, `kebab-case`, PascalCase and camelCase boundaries.
  - **Nothing in this repo filters completion at all today.** `CompletionEngine.Complete` returns *every*
    table in the schema — `TableSuggestions:95-113` yields one per `schema.Tables`, unfiltered — ranked only by
    `Priority` then name (`:70-73`). All as-you-type narrowing is AvaloniaEdit's:
    `CompletionController.OnTextEntered:54-55` deliberately bails while the window is open ("let AvaloniaEdit
    filter it"). Its scorer (`GetMatchQuality`, `CamelCaseMatch` — present in the 12.0.0 assembly but
    **private**, so not overridable) scores full match / match-start / substring / camel-case. The decisive
    point needs none of that detail: `al` is neither a prefix **nor a substring** of `accounting_lines`, so no
    branch of that scorer can match it and the item is filtered *out* of the list. This cannot be fixed by
    tuning what the engine emits.
  - **The scorer we want already exists in-repo.** `Input/PaletteFilter.Score(title, query)` (`:30-51`) is
    precisely this algorithm: subsequence match, +5 per contiguous character, +3 for a word start
    (`!char.IsLetterOrDigit(prev)` — so `_`, `-`, `.` and space all count), minus the index of the first hit.
    Traced by hand it already answers the request: `al` → 6, `accli` → 21, both matching. **The gap is case
    transitions** — it lowercases up front, so `AccountingLines` from `al` still matches but earns no
    word-start bonus and sinks below noise. Add a lower→upper boundary test against the *original* string.
  - The wiring is the actual work: set `CompletionList.IsFiltering = false` (the property does exist in
    AvaloniaEdit 12.0.0) and own the narrowing — `CompletionController` stops returning at `:55` and instead
    re-scores and re-populates the list per keystroke, which means also owning "nothing matches → close the
    window" and keeping the selection sane as the list shrinks. Insertion is unaffected:
    `BearingCompletionData.Text` is already filter-only, since `Complete()` inserts `ReplacementText`
    (`:20-32`).
  - Decide how the fuzzy score composes with the engine's `Priority` (tables 10, keywords 1) — specifically
    whether a strong hit on a keyword may outrank a weak one on a table. Keep the scorer **pure** so it's
    testable without a popup (§2.5, §4.3): generalize `PaletteFilter` rather than growing a second scorer, and
    pin the two reported cases as tests. *(live QA)* for the feel. Pairs naturally with the popup
    icons/styling item below — same list, same item template.
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
- [ ] **P2** **Per-tab font zoom.** The configurable *base* size shipped with the settings window
  (Settings ▸ Editor ▸ font size — the editor's hard-coded `FontSize="14"` is gone), so what's left is
  transient zoom on top of it: `Ctrl+=` / `Ctrl+-` bump the **current tab's** size, `Ctrl+0` resets to the
  base. Each tab keeps its own zoom, and the zoom is **not persisted** — reopening a tab, or opening a fresh
  one, starts from the base size again. Wire it as commands in the input pipeline (`CommandIds` +
  `KeymapDefaults`, §9.2), not an ad-hoc key handler; hold the size on `EditorTabViewModel` (not in
  `session.json`) and bind the editor's `FontSize` to it. Decide whether the results-grid font follows the
  same zoom or only the editor does.

### Panels, projects & dialogs

- [ ] **P2** Remove / delete projects. Delete a project from the recent list, and optionally from disk (with
  confirm). Recent-list pruning of *missing* directories already self-heals
  (`RefreshRecentAsync` + `IRecentProjects.RemoveAsync`), so this is the deliberate-removal half:
  a still-present project the user wants gone, and the on-disk delete behind a confirm.
---

## 🟡 Open — quality & maintainability

- [~] **P3** Clear build warnings — **4 remain** on a `--no-incremental` build: 2 × `xUnit2013`
  (`HistoryPanelTests.cs:51,53` — use `Assert.Single`) and 2 × `ANT01` from the vendored PostgreSQL lexer
  grammar (`PostgreSQLLexer.g4:1441,1456`, "non-fragment lexer rule can match the empty string"). The ANTLR
  pair was missing from the earlier count; it comes from vendored grammar, so decide whether to fix it
  upstream-style or suppress `ANT01` for that one file rather than leaving the number permanently non-zero.
- [~] **P3** Raw `ex.Message` surfaced to the UI on generic catch paths. **Partly done 2026-08-10, and
  deliberately narrower than written.** Pure `Core/Data/SafeErrorText` redacts `password=…` / `pwd=…` values
  out of driver messages — the real hazard, since a connect- or parse-time failure can quote the whole
  connection string, which then lands in the results pane, the status bar *and* the query log. Wired into the
  executor's three generic catches and the connect-failure path (`ConnectionSessionManager`).
  **Host/port/database are kept on purpose:** this is a local tool showing the user a server they configured
  themselves, the connect path already names the endpoint by design (`Could not connect to 'x' (host:port/db)`),
  and stripping it would remove the useful half of every DNS/TLS/network error while protecting nobody. If the
  endpoint should genuinely be hidden, that's a separate decision — say so and it's a one-line change.
  **The connection dialog's `OnTestClick` — the miss called out here — is now wired (2026-08-13), and the
  sweep for other UI-facing catches is done.** Result: everything else is either already safe or can't carry
  a credential. The file-I/O catches (`TabAutosave:176`, `WorkspaceViewModel:165`, `ScriptsViewModel:112-158`,
  `SettingsService:86`, `KeymapLoader:60,76`, `ShellViewModel.Projects:56`, `ExecutionViewModel:448` export)
  report `IOException` text; `ExecutionViewModel:232` catches `ConnectionFailedException`, whose message
  `ConnectionSessionManager` already redacted; `HistoryPanelViewModel:56` is the local SQLite log.
  **Two genuine candidates left**, both because `SchemaBrowser` opens its *own* connections, so a connect-time
  failure during a schema read can still quote a connection string: `SchemaNodes.cs:86` (the ⚠ child node on a
  failed tree expansion) and `SidebarView.axaml.cs:224` ("Could not load definition"). Both are one-line
  `SafeErrorText.Of(ex)` changes.

---

## 🔵 Open — hardening, security & distribution

- [~] **P1** **Windows + macOS secure credential storage — written 2026-08-13, verified on neither.** Both
  stores now exist behind the unchanged `ISecretStore`, and `SecretStoreFactory` dispatches per platform
  (`PlatformStore()`) instead of wiring libsecret only, so nothing above the store changed:
  `WindowsCredentialSecretStore` (Credential Manager via `CredWriteW`/`CredReadW`/`CredDeleteW`, keyed
  `bearing:connection:<guid>`, `CRED_PERSIST_LOCAL_MACHINE`, UTF-16 blob) and `MacKeychainSecretStore`
  (a login-keychain generic password via the `security` CLI, service = app dir name, account = guid).
  Availability is now decided by a **shared store→read→delete probe** in the factory rather than a
  per-store `IsAvailableAsync`, with a `finally` that never leaves a probe credential behind (it would be
  visible in both OS credential UIs).
  - **What's left is the part this box can't do: run it.** `tests/Bearing.Persistence.Tests/PlatformKeychainTests.cs`
    is the whole contract (round-trip, rotate-in-place, idempotent delete, per-connection isolation,
    awkward/Unicode password) written platform-agnostically over
    `SecretStoreFactory.CreatePlatformStoreAsync`, so **`dotnet test` on a Windows or macOS box is the
    verification** — 5 skip-safe tests that pass here against real libsecret and will exercise the new
    stores there. The first test also asserts the factory picked *this* platform's store, which is the quiet
    failure to watch for (falling through to the file fallback on a platform that has a keychain).
  - Also worth eyeballing on those platforms: the status bar reads "Secrets: OS keychain." and the
    connection dialog defaults to *Stored password* with no warning — that's `CanStore`/`IsSecure` true,
    i.e. the same posture Linux has today.
  - **Known trade-off on macOS:** `security add-generic-password` takes the password as an argument, so it
    is briefly visible in the process list (documented in the class). Its stdin-prompt mode reads from
    `/dev/tty` when one exists and would hang a terminal-launched app, and macOS restricts reading another
    process's arguments to the same user — who can already read the keychain item. The CLI is used rather
    than Security.framework on purpose: a keychain item's ACL is bound to the creating app's code signature,
    so an unsigned/self-updating Bearing would prompt after every rebuild and every Velopack update, while
    items owned by Apple-signed `/usr/bin/security` stay silent. Revisit if signing/notarization lands.
  - Windows `CredDelete` reports "nothing matched" honestly (`ERROR_NOT_FOUND`), so it doesn't need
    libsecret's postcondition re-read — but "delete, then it really is gone" stays asserted in the tests.
- [ ] **P2** **Velopack for distribution + in-app updates.** `build/release.sh` produces a self-contained,
  single-file `linux-x64` tarball with a local installer, a `.desktop` launcher and hicolor icons — one
  platform, and **no update path at all**: a new version means the user finds, downloads and re-installs it.
  Adopt [Velopack](https://velopack.io) for packaged installers plus delta auto-update on Windows, macOS and
  Linux.
  - What it wants that we don't have yet: a **real semver** — `release.sh` derives the version from a git tag
    *or a short sha*, and a sha isn't orderable, so "is there an update?" can't be answered; a **release feed**
    to publish to and point the app at (GitHub Releases is the cheap answer); and an **in-app update
    surface** — check, download, "restart to apply" — which is new UI and wants the same *(live QA)* tag as
    everything else.
  - Interactions worth deciding before writing code: it takes over app *layout* (Velopack installs and updates
    a versioned directory and manages the shortcut), which must not disturb `~/.config/bearing` /
    `~/.local/share/bearing` — user data lives outside the install and must survive an update untouched, and
    on Windows a DPAPI-or-Credential-Manager secret must too (see the item above). Decide whether the plain
    tarball stays for people who don't want an updater, and whether updates are opt-in (a setting — one
    property plus a catalog entry).
  - **Prerequisite-ish:** the P3 CI item below. Multi-platform packaging by hand on a Linux box isn't
    verifiable; a build matrix is what makes a Velopack release reproducible, and signing/notarization is the
    part that will actually bite on macOS.
- [ ] **P3** **The no-keyring warnings still assert a cause they never checked.** Both amber blocks
  (`ConnectionDialog.axaml:46,54`) and the status bar (`ShellViewModel.Projects.SecretPosture`) say "No system
  keyring **found**", which is now known to be wrong in at least one real case — the keyring was there and
  answering, the probe just ran too early. The reason is no longer thrown away (`SecretStoreFactory`
  → `CrashLog.Note`), so the remaining work is to carry it to the UI: reword to "couldn't be reached", and
  plumb the reason through `SecretStoragePosture` (a third field) so the dialog can show *why* — a locked
  collection and a missing helper want different advice, and only one of them is worth an unlock hint.
  - While there: the re-probe fires on every connection-dialog open on a machine that genuinely has no
    keychain (it's a no-op only once a keychain is adopted). That's the right trade — it's exactly the machine
    where healing matters — but if it ever shows up as a delay, cache the failure for a short interval rather
    than reverting to deciding once.
- [ ] **P2** Query-log privacy: file perms (0600), optional encryption, and/or PII/literal stripping.
  Retention exists (default 180d); no stripping. `SqliteQueryLog.cs`.
- [ ] **P2** Enforce/prompt TLS (`sslmode`) — currently only set if the user adds the option manually.
  The settings framework can carry the preference the moment the connection-level work exists; it's the one
  wanted option that isn't just a property plus a catalog entry.
- [ ] **P3** CI pipeline (build + `dotnet test`). Previously skipped; now also the thing the Velopack item
  leans on for multi-platform packaging.
- [~] **P1** Fallback secret storage — **the exposure is closed as of 2026-08-10, by not storing rather than
  by encrypting** (user's call over keychains / machine-key / passphrase encryption). With no OS keyring,
  `FileFallbackSecretStore` **refuses** a new password (typed `SecretStorageRefusedException`) instead of
  writing base64 to `~/.local/share/bearing/secrets/`; such connections keep the secret in memory for the
  session, a new connection defaults to *Prompt each time*, and a `StoredPassword` connection with nothing
  stored connects passwordless first (so trust auth / `.pgpass` still work) before the one-shot auth-retry
  prompts. Reads and deletes still work, so secrets written before the change keep resolving and can be
  cleared. The old behaviour is one opt-in away: Settings ▸ Security ▸ `AllowUnencryptedSecretFile`, read
  live, which keeps the amber base64 warning.
  **Still open:** actual encryption of that opt-in file. The platform stores it was waiting on are **written**
  (see the Windows/macOS item above), which shrinks the fallback to what it should be — the path for a machine
  with no credential store at all — but the file itself is still base64 when the opt-in is on. Off by default
  is what keeps that from being load-bearing.

---

## Notes

- **GUI can't be driven headlessly here (Wayland), so every UI change needs a manual pass** (§4.3). Never
  report a visual or interaction change as verified — say it builds, tests pass, and the user must eyeball it.
  Two surfaces can't be seen on the dev box at all: the connection dialog's no-keyring warning and
  Settings ▸ Security only appear on a machine *without* libsecret.
- **Keep file:line references honest** — re-check them when you touch an item. The drift to watch for is
  code moving between partials (`MainWindow`, `ResultView`, `ShellViewModel` are all composition roots over
  several files now), which silently invalidates pre-split line numbers.
- **Seams already built — use them instead of adding a parallel path:**
  - **Settings** — anything that wants to be configurable costs one `AppSettings` property plus one
    `SettingsCatalog` descriptor; it then renders, searches, persists and resets for free.
    `SettingsCatalogTests` fails the build if a property has neither a descriptor nor an explicit
    hidden-state entry, so the catalog can't drift behind the model.
  - **Tabular text/file formats** — a new format (Parquet, a `VALUES` list, `insert … on conflict`) is a
    function over `Results/TableBlock` in `TableFormats` plus one enum member added to
    `CopyRenderer.Alternatives`; it then works for **both** Copy as ▸ and Export, and is unit-testable
    without a grid.
  - **Grid actions** — `Controls/ResultContextMenu` is the discoverable home for anything new in the grid
    (Paste belongs there next to Copy). The tab strip has no equivalent yet; the tab-menu item above builds it.
  - **Confirming a write** — build a `Services/WriteConfirmation` (connection, action, verbs, statements) and
    the dialog derives all its display text from it; anything that wants to *show* SQL reuses
    `Controls/SqlStatementList`. Note the guarded path deliberately does **not** commit on Enter.
  - **Quit-time intervention** — hangs off `MainWindow.OnClosing`, whose block path deliberately does not
    call `base` (that is what raises `Closing`, which saves the session and disposes live connections —
    running it for a close that isn't happening would kill the queries the user just chose to keep).
  - **Keyboard** — register a command in `CommandIds` + `KeymapDefaults` and let `KeyDispatcher` route it
    (§9.2); never add an `OnKeyDown` branch. Grid *spatial* navigation is the one deliberate exception.
    Register a scope's commands **before** `keybindings.json` loads — the loader rejects bindings for ids it
    hasn't seen.
  - **Pure logic** — `Results/`, `Input/`, or `Bearing.Sql` (§2.5). On this project that isn't style advice:
    extracting the pure part is the *only* way to get behaviour under test, because the UI can't be driven.
  - **`ConnectionInfo.Options`** — app-level keys (`entra.*`) work now: the factory filters reserved
    credential/identity keys, ignores keys Npgsql doesn't own, and still applies genuine keywords.
