# Result-set overhaul — implementation plan (handoff)

Written 2026-07-17 to hand off across a context clear. Everything below the "DONE" line is
still to build. The auto-memory `squirrel-project.md` has the broader project state; this file is
the executable plan for the result-set work plus a few loose ends from the same session.

## How to work / verify

- Build app: `dotnet build src/Squirrel.Desktop/Squirrel.Desktop.csproj`
- Run app: `dotnet run --project src/Squirrel.Desktop`
- Tests (fish shell): `set -x SQUIRREL_TEST_PG_PORT 5434; dotnet test tests/Squirrel.<Proj>.Tests/Squirrel.<Proj>.Tests.csproj`
  - Projects: `Sql`, `App`, `Data`, `Persistence`. Current totals: 28 Sql, 12 App, 7 Data, 11 Persistence.
- Test DB: docker container `squirrel-pg-test` on **port 5434**, db `pagila`, user `postgres`, pw `squirrel`
  (FK-rich: film/actor/city/country/address). Integration tests SkippableFact-skip if unreachable.
- Rule: never block the Avalonia UI thread on async (`.GetAwaiter().GetResult()`), especially on the close path.

## Decisions (locked with the user)

1. **Read-only first**, then mutation. Order: (Phase 1) paging + row-numbers, (Phase 2) FK-click
   navigation, (Phase 3) inline edit/delete/insert.
2. **Row-numbers**: DONE (gutter via `LoadingRow`).
3. **Paging**: default page size **100**; a click shows the **total count**; a click **loads the next
   page and appends** (infinite-scroll style, rows accumulate).
4. **Inline edits reach the DB as PK-based generated statements** — editing is enabled ONLY when a
   result set maps to a single table with a detectable primary key.

## DONE (foundation already in place)

- **Statement scoping** (`src/Squirrel.Sql/StatementSplitter.cs`): run/highlight/completion operate on
  the statement at the caret. Statements split on `;` **or a blank line** (≥2 newlines at paren depth 0).
  Caret rule: a statement owns its `;` and trailing blank lines; caret switches to the next statement
  only at that statement's first char.
- **Executor paging primitives** (`src/Squirrel.Data/Postgres/PostgresQueryExecutor.cs`, interface in
  `src/Squirrel.Core/Data/IDbProvider.cs`):
  - `Task<QueryResult> ExecutePageAsync(string sql, int offset, int limit, ct)` — wraps
    `select * from (<sql>) _sq offset..limit..` (MaxRows=null so limit bounds it).
  - `Task<long?> CountAsync(string sql, ct)` — `select count(*) from (<sql>) _sq`; null if uncountable.
  - Both strip a trailing `;`. offset/limit are ints → injection-safe. Tested vs pagila (1000 films).
- **Row-number gutter**: `BuildResultView` in `src/Squirrel.App/Views/MainWindow.axaml.cs` sets
  `HeadersVisibility=All` and `LoadingRow += (_,e)=> e.Row.Header = index+1`.
- **Results UI shape**: result rendering was extracted into a self-contained `ResultView` control
  (`src/Squirrel.App/Controls/ResultView.cs`), hosted as `<controls:ResultView x:Name="ResultsView">` in
  `MainWindow.axaml`. `MainWindow.RebuildResults(...)` is now a one-liner: `ResultsView.Results = ...`.
  Rows are `object?[]`, columns bound by `[i]` indexer.

## Phase 1 — Paging UI  (DONE 2026-07-17)

Built on the **ResultSet VM** shape (chosen with the user — the clean end state; Phases 2–3 slot in):

- **`ResultSetViewModel`** (`src/Squirrel.App/ViewModels/ResultSetViewModel.cs`) — one per result set,
  wraps a `QueryResult` first page + `Rows` as an `ObservableCollection<object?[]>` (grows on load-more),
  `IsPageable`, `SourceSql`, `HasMore`, `TotalCount`, `Loaded`, `CanCount`, `FooterText`, `AppendPage(...)`.
  Also carries `Success`/`Message`/`Error`/`Columns` so it renders the non-grid cases too.
- **`EditorTabViewModel.Results`** (IReadOnlyList<ResultSetViewModel>) replaced the old raw
  `LastResults`; `LastResult` now returns the first `ResultSetViewModel`.
- **`MainWindowViewModel`**: `ExecuteAsync` runs the first page with `QueryOptions{MaxRows=PageSize}`
  (`const int PageSize = 100`), then `pageable = results.Count==1 && [0].Success && [0].Columns.Count>0`;
  builds one VM per set (only the single pageable set gets `SourceSql`). New `LoadMoreAsync(rs)` /
  `CountTotalAsync(rs)` resolve the selected tab's already-live session via `_sessions.TryGet` and reuse
  the `_executionCts`/IsBusy pattern.
- **`ResultView`**: pageable single-set grids get a bottom footer (`FooterText` + `[Load more]` bound to
  `HasMore` + `[Count]` bound to `CanCount`). Grid `ItemsSource` binds to `rs.Rows` → append with no
  rebuild. `MainWindow` wires `ResultsView.LoadMore`/`CountTotal` to the VM methods at construction.
- **Test**: `WorkspaceFlowTests.Single_select_pages_and_counts` drives ExecuteAsync → LoadMore → Count
  against pagila.film (1000 rows); asserts 100 → 200 loaded, total 1000, multi-statement not pageable.

**Tradeoff / gotcha**: the whole run is now capped at `PageSize` (100), so a *multi-statement* run's grids
show ≤100 rows each with no load-more (previously 10 000). Truncation still surfaces in the status line.
**Not visually QA'd** (no headless GUI) — verify the footer, Load more append, and Count live.

### Prior foundation (kept)

---

## Phase 1 — Paging UI  (SUPERSEDED — see the DONE section above for what shipped)

The original design notes below are kept for context; the actual implementation used a per-result-set
`ResultSetViewModel` (see above) rather than a bare `PagedResultState`.

Goal: default show 100 rows; footer under the grid with row count, `[Load more]` (append), `[Count]`.

Key problem to solve first: **appended rows must persist across tab switches and append smoothly.** The
grid is currently rebuilt imperatively from `tab.LastResults`, and `QueryResult.Rows` is immutable. So:

1. Introduce a per-result **stateful holder** the grid binds to. Simplest: a small class (e.g.
   `PagedResultState`) owned by `EditorTabViewModel`, only for the pageable single-SELECT case:
   ```
   ObservableCollection<object?[]> Rows;   // grows on load-more
   IReadOnlyList<ColumnDescriptor> Columns;
   string SourceSql;                        // the exact SELECT that produced this (for paging/count)
   bool HasMore;                            // last page came back full
   long? TotalCount;                        // null until [Count] clicked
   int Loaded => Rows.Count;                // == offset for next page
   ```
2. In `MainWindowViewModel.ExecuteAsync`: after a run, decide pageability. Enable paging ONLY when
   `results.Count == 1 && results[0].Success && results[0].Columns.Count > 0` (a single row-returning
   result). Store the run's sql (the value passed to `ExecuteAsync`, i.e. the statement-at-caret text) as
   `SourceSql`. First page runs the RAW sql with `MaxRows = 100` (NOT wrapped — wrapping loses column
   base-table metadata needed in Phase 2). `HasMore = results[0].Truncated` (Truncated already means "more
   rows existed beyond the cap"). Keep page size as a const (e.g. `const int PageSize = 100`) and pass
   `new QueryOptions { MaxRows = PageSize }`.
3. VM methods (resolve the selected tab's session via `_sessions`, like `ExecuteAsync` does):
   - `LoadMoreAsync(tab)`: `var page = await session.Executor.ExecutePageAsync(state.SourceSql, state.Loaded, PageSize, ct); foreach(row) state.Rows.Add(row); state.HasMore = page.RowCount == PageSize;`
   - `CountTotalAsync(tab)`: `state.TotalCount = await session.Executor.CountAsync(state.SourceSql, ct);`
   - Reuse the `_executionCts`/IsBusy pattern; these are cancellable too.
4. UI (`BuildResultView` / `RebuildResults`): when pageable, wrap the grid in a `DockPanel` with a bottom
   footer `Border`: text "`Loaded {Rows.Count}{(HasMore?"+":"")}{(Total!=null? " of "+Total:"")} rows`",
   a `[Load more]` button (visible when `HasMore`), a `[Count]` button (hidden once Total known). Bind the
   grid `ItemsSource` to `state.Rows` (the ObservableCollection → smooth append, no rebuild). Wire button
   clicks to the VM methods; update footer text after each. Keep the multi-result TabControl path unpaged.
5. Tab switch: `RebuildResults` should rebind to the tab's existing `PagedResultState.Rows` so appended
   rows and scroll survive. (Consider whether `LastResults` and `PagedResultState` should be unified — a
   `ResultSet` VM per result set is the clean end state; a full MVVM refactor of the results area is
   optional but would make Phases 2–3 easier.)

Gotchas: `SourceSql` must be a single SELECT for wrapping to work — that's guaranteed by the
`results.Count == 1 && Columns>0` gate plus statement-at-caret. Don't offer paging for multi-statement runs.

Tests: `ExecutePageAsync`/`CountAsync` already covered in `PostgresExecutorTests`. Add an App-level test if
paging state moves into a VM method (drive `ExecuteAsync` then `LoadMoreAsync` against pagila).

## Phase 2 — FK-click navigation  (DONE 2026-07-17)

Behavior: FK columns show the value as plain text with a clickable **↗ jump-icon** aligned right.
Clicking it navigates **inline**: the lookup (`select * from <ref> where <refcol> = <value>`) runs on the
current tab's connection and its result **replaces the displayed table in place** — no new editor tab,
the query is never surfaced. The previous result is stashed on a per-tab history stack; a slim **Back**
bar (shown while history is non-empty) pops back to it. Navigation chains (the referenced result is
itself pageable + FK-aware), so Back can unwind multiple levels. Icon hidden when the cell is null.

Wiring: `EditorTabViewModel` owns the history (`SetFreshResults`/`PushResults`/`GoBack`/`CanGoBack`);
`NavigateForeignKeyAsync` runs the lookup and `PushResults`; `ResultView` renders the Back bar and
raises `GoBack`; `MainWindow.RebuildResults(tab)` pushes both the frame and the back-state.

UI gotcha (verified live): the app font renders symbol glyphs (`↗` U+2197, `✕` U+2715) **clipped** —
so the FK jump-icon and the tab close-✕ are drawn as vector `Path` geometry, not font glyphs. Use
`Path` for any future icon rather than a Unicode symbol in a TextBlock/Button.

Key deviation from the notes below — **column origin is (table OID + attnum), not base names.**
`NpgsqlDbColumn.GetColumnSchema()` leaves `BaseTableName`/`BaseColumnName` **null**, but `TableOID` +
`ColumnAttributeNumber` come free from the wire RowDescription (no catalog round-trip). So:

- **`ColumnDescriptor`** (`QueryResult.cs`) gained `uint BaseTableOid` + `short BaseColumnAttNum`
  (both 0 for expression/aliased columns) and a `HasBaseColumn` helper. Populated in
  `PostgresQueryExecutor.ReadColumns(reader, withBaseTables)` — `withBaseTables:true` only for the raw
  `ExecuteAsync` path, skipped for `ExecutePageAsync` (wrapped subquery has no origin).
- **`ForeignKeyResolver`** (`src/Squirrel.Core/Schema/ForeignKeyResolver.cs`, pure/unit-tested) — given
  the snapshot + result columns + clicked index, returns a `ForeignKeyTarget(RefSchema, RefTable,
  RefColumns, SourceColumnIndices)` or null. Only the *referencing* side navigates; composite FKs
  require every key part present in the result row (else not navigable).
- **`MainWindowViewModel`**: `DetectForeignKeyColumns(snapshot, columns)` fills
  `ResultSetViewModel.ForeignKeyColumns` at run time; `NavigateForeignKeyAsync(rs, colIndex, row)`
  resolves the target, builds the SELECT (`BuildForeignKeySelect` + `SqlLiteral`/`QuoteIdent`), opens a
  new tab (inherits connection), and runs it.
- **`ResultView`**: FK columns become `DataGridTemplateColumn` link cells; `NavigateForeignKey` callback
  wired in `MainWindow` (also refreshes the editor after the new tab runs).

**Tradeoff / not-done**: values are **inlined as SQL literals** (numbers bare; everything else single-
quoted with `''` escaping), NOT parameterized — chosen so the new tab shows a readable, editable query.
Values originate from the DB, but exotic types (arrays, some temporals) may need manual tweaking.
**Not visually QA'd** — verify the link affordance, click → new tab, and cursor live.

Tests: `ForeignKeyResolverTests` (Sql, 4 cases), `PostgresExecutorColumnTests.Raw_query_columns_carry_
base_table_origin` (Data), `WorkspaceFlowTests.Foreign_key_cell_navigates_to_referenced_row` (App, live).

### Original design notes (superseded by the OID approach above)

Goal: clicking a cell in a foreign-key column opens the referenced row.

1. **Column → catalog mapping** (also the foundation for Phase 3). Extend `ColumnDescriptor`
   (`src/Squirrel.Core/Data/QueryResult.cs`) with optional `BaseSchema`, `BaseTable`, `BaseColumn`.
   Populate in `PostgresQueryExecutor.ReadColumns` from `NpgsqlDataReader.GetColumnSchema()` →
   `NpgsqlDbColumn.BaseSchemaName/BaseTableName/BaseColumnName`. NOTE: this metadata only exists for the
   RAW query, not the wrapped paging query — so it's captured on the first page (Phase 1 step 2 already
   runs the first page raw). `GetColumnSchema()` is sync; fine to call before reading rows.
2. **FK detection**: for a clicked column, look up its `(BaseSchema,BaseTable,BaseColumn)` in the schema
   snapshot; find an FK where that column is the referencing (parent) column
   (`schema.ForeignKeysTouching(oid)` / `PgForeignKey.ParentOid/ParentAttNums`). The referenced table+col
   is the nav target. Need catalog oid for the base table (`schema.ResolveTable(baseSchema, baseTable)`).
3. **UI affordance**: render FK cells as clickable (link style / hand cursor). On click, run
   `select * from <refSchema>.<refTable> where <refCol> = <cellValue>` (parameterized) and show the result
   — open it in a new result tab, or a new editor tab, or a popup. Simplest first cut: open a new editor
   tab pre-filled with that SELECT and run it (reuses existing execution + grid). Decide with user if
   ambiguous.
4. The current grid uses `DataGridTextColumn` with `[i]` bindings; to make specific columns clickable
   you'll likely switch FK columns to `DataGridTemplateColumn` with a `Button`/hyperlink, or handle
   `CellPointerPressed`/selection + a context action. Keep non-FK columns as text.

Gotchas: arbitrary SQL (joins, expressions) yields columns with no base table → those simply aren't
FK-navigable. Composite FKs: build the WHERE with all key columns from the same row.

## Phase 3 — Inline edit / delete / insert (PK-based)

Goal: editable grid; changes become generated `UPDATE`/`DELETE`/`INSERT` keyed by PK.

1. **Editability gate**: enable only when all result columns share one `BaseTable` (single-table select)
   AND that table has a primary key present among the result columns. Reuse the Phase-2 `ColumnDescriptor`
   base mapping. Expose a per-result `IsEditable` + the PK column indices + target table.
2. **Statement generation** (new `src/Squirrel.Sql` or `Squirrel.Data` helper, unit-testable):
   - UPDATE: `update <t> set <col>=@p... where <pkcol>=@k...` from the edited cells + original PK values.
   - DELETE: `delete from <t> where <pkcol>=@k...`.
   - INSERT: `insert into <t> (<cols>) values (@p...)` for a new row; read back generated keys with
     `returning *` to refill the row.
   - All parameterized (NpgsqlParameter), never string-interpolated values.
3. **Apply model** (user chose PK-based; confirm commit UX — earlier they leaned "PK-based statements"
   without explicitly picking review-vs-autocommit). Recommend: collect edits into a pending set per grid
   and apply on an explicit **[Save changes]** (safer, one transaction). Show pending row markers.
   Executor needs a write path — add `Task<QueryResult> ExecuteNonQueryAsync(sql, NpgsqlParameter[], ct)`
   or a batch method; run the pending changes in one transaction; refresh the affected rows on success.
4. Grid: set `IsReadOnly=false` for editable results; handle `CellEditEnding` to record edits; add
   row-add / row-delete affordances (toolbar buttons or context menu). Track original PK values per row so
   edits/deletes target the right row even after edits.

Gotchas: NULL vs empty string in edited cells; type coercion (grid gives strings, DB wants typed params —
use the column CLR type from `ColumnDescriptor.ClrType`); optimistic concurrency (WHERE on PK only, or add
all original values); read-only/computed columns; views are not editable.

---

## Other pending / deferred (same session)

- **Verify terminal Ctrl+C saves the session.** `App.axaml.cs` now saves on `window.Closing`,
  `desktop.ShutdownRequested`, and `AppDomain.CurrentDomain.ProcessExit` (dropped the buggy
  `PosixSignalRegistration`). The ProcessExit-on-Ctrl+C path was NOT verified live (an earlier test was
  contaminated by a second running instance). Verify: with no other squirrel instance running, launch,
  edit, Ctrl+C the terminal, relaunch, confirm session restored. Session file:
  `~/.local/share/squirrel/projects/default/.squirrel/session.json`.
- **Visual QA pass** — these landed but were never eyeballed (no headless GUI): statement gutter bar +
  Alt+Up/Down nav; result-set sub-tabs; both tab strips' bottom-underline restyle; the amber ● unsaved
  marker; translucent text selection; row-number gutter; the autocomplete behavior (alias-dot columns +
  FK-equality predicate).
- **Stale connection**: the pre-existing `~/.local/share/squirrel/projects/default/project.json` may still
  point a connection at old port 5433; the demo seed skips non-empty projects. Fix in-app or edit to 5434.
- **M7 hardening** (from the original plan): query cancellation (DONE — Esc/`_executionCts`); large-result
  streaming (partially addressed by paging); schema snapshot refresh (no refresh button yet); packaging /
  AppImage. `PostgresQueryExecutor.StreamAsync` exists but is unused.

## Key files

- `src/Squirrel.Core/Data/QueryResult.cs` — `QueryResult`, `ColumnDescriptor`, `QueryOptions`, `ResultBatch`.
- `src/Squirrel.Core/Data/IDbProvider.cs` — `IQueryExecutor` (Execute/ExecutePage/Count/Stream).
- `src/Squirrel.Data/Postgres/PostgresQueryExecutor.cs` — executor impl (`ReadResultSetAsync` is the shared row reader).
- `src/Squirrel.App/ViewModels/MainWindowViewModel.cs` — `ExecuteAsync`, `_executionCts`, `_sessions`, `LastResults` wiring.
- `src/Squirrel.App/ViewModels/EditorTabViewModel.cs` — per-tab `LastResults`, `IsDirty`, `MarkSaved`.
- `src/Squirrel.App/Views/MainWindow.axaml{,.cs}` — `RebuildResults`/`BuildResultView`, `ResultsHost`, keybindings, editor setup.
- `src/Squirrel.Sql/StatementSplitter.cs` — statement segmentation (`Split`, `StatementAt`).
