# Project-Specific Rules (§9.x)

## §9.1 — God objects: extract types, don't grow files
`ShellViewModel`, `ResultView.*.cs` and `MainWindow.*.cs` are composition roots. Splitting into partial
files does not count as extraction.
- Pure logic → helpers under `Results/`, `Input/` or `Sql` (`GridSelectionOps`, `ResultSetBuilder`,
  `PaletteFilter`). Visuals → own `Views/`/`Controls/` class. Stateful coordination → a coordinator.
- Keep the public binding/callback surface; leave thin delegating members.
- Code-built visuals resolve brushes via `Controls/Tokens` (`Res` / `Tint`); no private `FindResource`.
  `Theming.ThemeBrush.AtAlpha` is the one exception.

## §9.2 — Input goes through the unified pipeline
- `src/Bearing.App/Input/` (`GestureParser`, `Keymap`, `CommandRegistry`, `KeyDispatcher`, `CommandIds`,
  `KeyScope`). Views call `TryHandle(e, scope)`.
- New shortcut = command + default binding in the keymap, never a new `OnKeyDown` branch. Grid spatial
  navigation is the exception.
- User overrides: `keybindings.json` via `KeymapLoader` over `KeymapDefaults`.

## §9.3 — Avalonia
- v12.1. Drag & drop: typed `DataTransfer`/`DataFormat`, `DoDragDropAsync`.
- Tokens in `Themes/Tokens.axaml`; dark Kanagawa in `App.axaml`; accent via `App.SetConnectionAccent(hex)`.

## §9.3a — Connection state = the beacon, on its own palette
- **Environment** palette (rose/gold/mint) owns **surfaces only**. **State** palette (`Status.*`) owns
  **icon strokes and label text only**, never fills; the env hue never colours a beacon.
- `Controls/ConnectionBeacon` is the one state mark (silhouette carries state). Don't draw a new glyph;
  don't go below 12px. Its `State` is a tri-state `ConnectionState`, not a bool.
- Toolbar toggle is `Icon.Power`, one glyph in all states.
- Avalonia `StrokeDashArray` is in units of `StrokeThickness`.

## §9.4 — Server link vs per-database pool
- Pools are per (connection, database); "connected" is per connection. Every user-visible connected
  indicator reads `ConnectionSessionManager.IsLinked(Guid)`, never `TryGet`/`IsAnyLive`.
- Link set by a successful `BuildAsync` on any database. Cleared by `EvictConnectionAsync`, last-session
  `EvictAsync`, failed connect with nothing live, credential near expiry, `CloseAllAsync`. **Not** by the
  idle sweep.
- `SetTabDatabase` warms the new pool in the background only when already linked (never prompts).

## §9.4a — Sessions keyed by `SessionKey` (connection id + database)
- Switching database must not rebuild a pool; only real settings changes do (`SameConnection`).
- `EvictAsync(SessionKey)` for one database; `EvictConnectionAsync(Guid)` for server-level actions
  (Disconnect, edit, delete, refresh, project close).
- `LiveChanged` carries the `SessionKey`. `MaxPoolSize` capped at 10 per pool.

## §9.5 — antlr4-c3 completion
One `CodeCompletionCore`, per-dialect `ISqlParseRules` (`PgParseRules` / `TSqlParseRules`).
- Preferred rules: prefer the rule that **contains** the name (`expression`), keep inner rules where no
  container reaches (`full_column_name` for `update … set`). A rule only reachable under another preferred
  rule is dead.
- Treat "adding rule X silenced rule Y" as unverified until probed.
- WHEN touching a rule set, probe real carets and keep the probe as a test. Popup **size** is asserted
  (`A_predicate_popup_is_short_and_column_led`), not just intent.

## §9.6 — Velopack
- Pack id is `BearingSql`, **not** `bearing` (installer deletes `%LocalAppData%\<packId>` on uninstall,
  where user data lives). It's also the permanent update identity — never rename.
- `VelopackApp.Build().Run()` stays first in `Bearing.Desktop/Program.cs`. Updating lives behind
  `IUpdateService` in `Bearing.Updates`.
- macOS: arm64-only (one package per channel; Intel needs a second channel), ad-hoc signed; Developer ID
  signing via `SIGN_APP_IDENTITY` / `SIGN_INSTALL_IDENTITY` / `NOTARY_PROFILE`, passed only when set.
  Homebrew cask `packaging/homebrew/bearing.rb`.
- `build/velopack.sh` must run on **bash 3.2**: expand arrays as `${ARR[@]+"${ARR[@]}"}`.
- Workflow jobs calling `gh` need `GH_REPO`. `.nupkg` names carry the channel except on `win`
  (`$PKG_CHANNEL_TAG`).
- No `PublishSingleFile` for Velopack builds. Apply updates through the normal window close
  (`UpdateCoordinator.RestartToApply`).

## §9.6a — Version = git tag; releases publish themselves
- Publishing a GitHub Release triggers `.github/workflows/release.yml`. See `docs/RELEASING.md`.
- MinVer derives the version. DO NOT add `<Version>` or "bump the version". Dev builds are
  `x.y.z-alpha.0.N`; `ALLOW_UNTAGGED=1` packs `<tag>-local.<sha>`.
- Keep the post-upload asset verification and the failed-job return-to-draft.
- Pre-releases must reach `vpk upload --pre` (re-asserted after).
- Notes: `docs/release-notes/<version>.md` wins; otherwise leave a web-typed description alone.

## §9.7 — Demo mode is a whole session
`--demo` / `BEARING_DEMO`: `DemoProvider` replaces Postgres in the registry for the process.
- Isolation: `App.DemoWorkspace` points every store at one temp dir (deleted on close) with
  `NoSecretStore`. Never touch `BearingPaths.DataDir`/`ConfigDir`, recent projects, the real query log or
  the keychain.
- Demo connection keeps `RequireWriteConfirmation`. Ships in Release. The empty-state button relaunches.
- `DemoWorkspace.DisposeAsync` closes the query log first.

## §9.8 — Late tree reads that add rows start in `OnChildrenAttached`
`EnsureChildrenAsync` clears `Children` after `LoadChildrenAsync`, so appends belong in
`OnChildrenAttached`. Relabel-only reads (`FillSizesAsync`) are fine elsewhere. Raise an internal event
(`SizesLoaded`, `ObjectKindsLoaded`, `RolesLoaded`) so tests can wait.

## §9.9 — Explorer object kinds (#119 / #120)
- `GetDatabaseObjectsAsync` returns `DatabaseObjectKinds` in one late call (§9.8); each kind is
  best-effort on its own (`Best`) — so a broken query looks empty; only a live test catches it.
- Policies shown per table and per database. Procedures are their own group. Roles hang off the server.
- Postgres facts: exclude `relkind = 'c'` composite types; sequence ownership uses `deptype in ('a','i')`;
  pagila's sequences have no `OWNED BY`; a sequence's type is its own; null `last_value` ≠ 0.
- Don't write `$1 is null or …` with an untyped null — append the predicate instead.

## §9.9a — Explorer placement
- Defaults, identity, generated, collation, comments → `TableDetails`, not `ColumnInfo` (snapshot is the
  completion hot path). Generated never also reports a default; identity outranks default; only
  non-default collations shown. Comments go through `RelationDetailText.WithComment`.
- Partitions are children (`TableInfo.PartitionOf`, in the snapshot); orphaned partitions stay top-level.
- Rules exclude `_RETURN`. Tablespaces on the server node; event triggers, publications, foreign servers
  per database.
- Long-tail kinds share `SchemaObjectInfo` + `SchemaObjectNodeViewModel`; graduate to a record only when a
  field is used. `DatabaseObjectKinds` uses init-properties with empty defaults.

## §9.9b — Tree pitfalls
1. Late appends need a generation guard (`LoadGeneration` / `IsCurrentLoad`).
2. Pass hierarchies whole (`PartitionMap`, indexed), not sliced.
3. `left join` on one-to-many catalogs (`pg_inherits`) fans out — use a scalar subquery.
4. Model subscriptions must be detached when the visual is rebuilt (`_layoutSubscriptions`).
5. Write-time invariants need enforcing on existing state too (`DropHiddenColumns`).
6. Name coordinate spaces (visible-column count vs `DisplayIndex`; `ResultColumnMenu.FreezeThrough`).

## §9.10 — Freezing/hiding columns (#118)
- `ColumnLayout` per result set, applied in place (`ApplyColumnLayout`) — never rebuild the grid.
- Last visible column can't be hidden; freeze leaves ≥1 scrollable column.
- Hidden columns are unselectable (`GridSelectionOps.FirstColumn/LastColumn/StepColumn/Rectangle/
  AllCells`) but **still exported**; `HiddenColumnsText` marks it.
- Header menu `ResultColumnMenu` resolves from `ContextRequested` in the tunnel phase.

## §9.10a — Focus arriving in a grid is not a viewport event (#60)
- `SeedActive` seeds `FirstCellInView` with `scroll: false` (filter on `IsEffectivelyVisible` + non-zero
  size).
- A press is never a scroll; a commit is never a scroll. Only cursor motion (`MoveActive`) and
  `BeginEditActive` scroll.
- `SelectBand` / corner: write the model before taking focus; band origin stays the anchor, cursor is pulled
  into view (`InViewRow`/`InViewColumn`).
- `WireSelection` claims Tab's KeyDown **and** KeyUp in tunnel (except in a cell editor).
- Tests: `KeyPress` is down-only (send `KeyRelease`); click a different cell from the one you scrolled to;
  use `WideNumericResult` for stats-bar tests.

## §9.10b — Selecting a tree row must not scroll sideways
`TreeChrome` rewrites `TreeViewItem` bring-into-view to a zero-width rect at an x inside the viewport
(vertical reveal kept for F12). Class handler must run after Avalonia's (`RunClassConstructor` in the
static init); scoped to trees `Apply` was called on and to requests targeting the row container itself.
Tests must start from a non-zero horizontal offset.

## §9.10c — Digit grouping is display-only
`NumberGrouping` has exactly three callers: `ResultCellFactory.Shown`, `ResultCellFactory.InitialWidth`,
`CellStats.Format`. A fourth is a design question.
- **Never in `CellFormat.Display`** (clipboard, exports, DML, inspector, editor seed, `Coerce`).
- Fixed comma separator, invariant decimal point. Operates on the rendered string; non-matching text
  untouched. Columns follow `CellStats.IsNumeric` (no `BigInteger`) and CLR type (FKs group, text doesn't).
- A refused numeric edit is drawn amber. **Do not accept the grouped form on input.**
- `ColumnWidths.Sample` measures the grouped text. `NumberGrouping.Enabled` is app-global; `Apply` also
  takes the flag explicitly for tests.

## §9.11 — Go to definition (#117)
- `GoToDefinition.Resolve(sql, caret, snapshot)` is pure; caret position in a dotted chain decides the
  answer; aliases via `FromClauseExtractor`; returns null rather than guessing.
- `ConnectionsViewModel.RevealRelationAsync` descends (matching via `SchemaTreeReveal`) and returns
  `SchemaRevealResult`. Match schema + name, never `Title`.

## §9.12 — Tab strip
- `▾ N` dropdown (`TabOverflowMenu`, the button's `Flyout`), always visible; count shown only when tabs are
  hidden. Fill it from `PointerPressed` (tunnel) and `Refresh()` — never from `Opening`. `TabStripOverflow`
  is built after `_dispatcher`. `Toggle` refreshes posted. Test realized `MenuItem`s, not `IsOpen`.
- Ctrl+E picker, and `tab.expandStrip` (scrollbar `Disabled` → `WrapPanel` wraps; per-row `MaxHeight`).
- Keep the #65 layout-loop safeguards: width reserved from the count, fixed 40px button, `Sync` writes only
  on change.
- Drag reorder (`TabDragReorder`): pointer capture, not `DoDragDropAsync`; move on release only; label is
  the handle; hit-test row then X (`TabReorder.Hit`); release beyond `DropTolerance` moves nothing; Escape
  aborts; edge auto-scroll is a timer. Slot → index via `TabReorder.TargetIndex`.
  `WorkspaceViewModel.MoveTab` is the one mutation (re-pin with no move still calls `ResplitTabs`).

## §9.13 — Activity panel (#101)
`pg_stat_activity` every 2.5 s while visible; Cancel/Terminate on a row.
- Behind `IServerActivity`, not `IMetadataReader` (which stays read-only).
- Every action confirms; `IsCancel` button is also `IsDefault`; `ConfirmBackendActionAsync` defaults false.
- Terminate refused on read-only (`WriteRefusal.ReasonForTerminate`); Cancel allowed.
- Polling driven by `ShellViewModel.SyncPanelActivity` from both `OnActivePanelChanged` and
  `OnSidePaneOpenChanged`.
- Reads: `Sessions.TryGet` only, a lease per read, per-read deadline, skip overlapping ticks. Actions
  re-resolve the session after confirmation. A failed read keeps old rows. Row identity = pid +
  `backend_start`.
- Default scope: own database, non-idle (`state is distinct from 'idle'`); status line names the scope.
  Append predicates, not `$1 is null or …`.
- Neither the poll nor the actions are logged. Own control (`Controls/ActivityPanelView`).

### §9.13a — Restricted roles don't see other sessions at all
Ask `ServerActivity.SeesAllSessions` separately and say so in the status line. WHEN a read depends on role
privileges, measure what the server withholds before designing around it.

## §9.14 — Database node shapes (#132)
`AppSettings.SchemaTreeMode`: **Simple** (default — relations inline, long tail in `Other objects`) or
**Full** (schema-first, database-wide kinds in `Administer`).
- `Arrange()` is the only builder, from cached data; a toggle re-reads nothing. Mode read live
  (`Func<SchemaTreeMode>`). Expansion below the database row isn't preserved.
- Kinds landing: Simple appends the bucket; Full re-arranges. Full collapses the schema level for a
  single-schema database. Decisions live in pure `SchemaTreeShape`.

### §9.14a — Deep relations
`DatabaseNodeViewModel.Relations` and `SchemaTreeReveal.RelationsUnder` recurse over materialised nodes
only. Sizes are cached (`_sizes`) and applied as rows are built (`ApplyCachedSizes`). Reveal expands only
the named schema.

### §9.14b — Counts
Landed-and-empty shows `—`; not-landed isn't built. Empty buckets are omitted. `Other objects` counts
objects, not sub-groups.

## §9.15 — Open cell editor isn't pending until committed (#147 / #148)
- `ResultView.CommitOpenEdit` runs before anything reads the pending set (save/discard/show-SQL, buttons and
  commands).
- Focus leaving the grid commits; Escape cancels.
- `CommitFirstCommands` (`grid.save`, `grid.discard`, `grid.showSql`): resolve the key, commit, then dispatch
  with that set as `only`. Don't add Ctrl+C/V/arrows.
