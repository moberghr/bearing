# Project-Specific Rules (§9.x)

## §9.1 — God objects: extract, don't grow
**All three god objects were decomposed (2026-08-10). Keep them that way.** Splitting a class into partial
*files* is what previously hid the problem: the line count moved, the concerns didn't. Extract *types*.

The three, and the pattern each now demonstrates:
- `ViewModels/ShellViewModel` (was `MainWindowViewModel`, 1,014) → child VMs behind `WorkspaceContext`.
- `Controls/ResultView.*.cs` (1,902 → ~500 over 3 partials) → a composition root that assembles
  `GridSelectionController`, `ResultCellFactory`, `ResultChrome`, `CellInspectorView` + `InspectorPane`,
  `ResultEditToolbar`, `ResultGridChrome`, `QuickStatsBar`, `ResultRowPainter`, plus the pure
  `Results/{GridSelectionOps, ResultMetaText, ColumnKinds}`.
- `Views/MainWindow.*.cs` (1,167 → ~820 over 5 partials) → `.Commands` is the command table + key routing,
  `.Chrome` the XAML-wired handlers; behavior lives in `Editing/EditorTextCommands`, `EditorChrome`,
  `Views/CommandPaletteHost`, `Views/ResultsPaneController`, `Input/{TabNavigator, FocusRing}`.

WHEN a change would add code to any of these, DO NOT append — extract:
- Pure/stateless logic → helpers under `Results/`, `Input/`, or the `Sql` project (pattern:
  `GridSelectionOps`, `ResultSetBuilder`, `ResultEditModel`, `PaletteFilter`). Still the first move for
  getting behavior under test — faster to run and to read — but no longer the *only* way: headless UI tests
  landed 2026-08-31 (#62), so a claim that only a realized visual can hold (a brush on a live cell, a scroll
  offset after a layout pass) is testable where it lives (§4.3, §4.5).
- Self-contained visuals/overlays → their own `Views/`/`Controls/` class.
- Stateful coordination (connections/execution/tabs/panels) → a dedicated coordinator.

Keep the public binding / callback surface unchanged when splitting — leave thin delegating members behind.

Code-built visuals resolve token brushes through `Controls/Tokens` (`Res` / `Tint`) — do not reintroduce a
private `FindResource` helper; six of them were consolidated. `Theming.ThemeBrush.AtAlpha` is the one
exception (it takes an explicit fallback colour, for converters and custom margins).

## §9.6 — The Velopack pack id is NOT `bearing`
`build/velopack.sh` packs with `--packId BearingSql --packTitle "Bearing"`. The Windows installer owns
`%LocalAppData%\<packId>` and **deletes it on uninstall**, and `BearingPaths.DataDir` on Windows is
`%LOCALAPPDATA%\bearing` (query log, default project) — a pack id of `bearing` would put the install root on
top of the user's data and take their query history with it on uninstall. Do NOT "tidy" the id to match the
binary name.
- `packId` is also the permanent update identity: renaming it orphans every installed client (each needs a
  manual re-install), so it is not a cosmetic string.
- `VelopackApp.Build().Run()` stays the first statement in `Bearing.Desktop/Program.cs` — `vpk pack` verifies
  it is in the entry assembly and refuses to package without it. Everything else about updating lives behind
  `Bearing.Core.Updates.IUpdateService` in `Bearing.Updates`.
- Velopack builds publish **without** `PublishSingleFile` (deltas are per-file; one compressed exe makes every
  update a full ~65 MB download). `build/release.sh`'s single-file archive path is separate and unchanged.
- Applying an update goes through the ordinary window close (`UpdateCoordinator.RestartToApply`), never
  `ApplyUpdatesAndRestart` from under the UI — the shutdown pipeline is what saves the session.

## §9.6a — The release version is the git tag, and releases publish themselves
A release is cut by **publishing a GitHub Release** — nothing else (#125, docs/RELEASING.md).
`.github/workflows/release.yml` fires on `release: published`, tests, then builds and uploads both platforms
from one runner.

- **There is no `<Version>` property.** MinVer derives the version from the nearest `v*` tag and feeds
  `AssemblyInformationalVersion`, which is what `AppVersion` (and so `Help ▸ About` and the update check)
  reads. DO NOT reintroduce a `<Version>` to `Directory.Build.props` and DO NOT "bump the version" for a
  release: the property and the tag are exactly the two things that drifted before — `v0.5.4` landed on the
  commit *before* the bump, so the tag said 0.5.4 while the build said 0.5.3.
- A working build calls itself `0.5.5-alpha.0.3`, and that is correct: it is not a release, and a dev build
  must not impersonate the last one. `ALLOW_UNTAGGED=1` packs `<last-tag>-local.<sha>` for the same reason.
- `velopack.sh` **verifies after uploading** that `releases.<channel>.json` and the full `.nupkg` are on the
  release, and fails if not. That check exists because `v0.5.4` shipped with *no assets at all*: the page
  read "Latest" while every installed copy correctly stayed on 0.5.3, and nothing anywhere noticed. Keep it.
- A failed job **returns the release to draft**. Publishing is the trigger, so the alternative is a live
  release page with nothing on it — see above for what that costs.
- A **pre-release** must reach `vpk upload --pre`, and the flag is re-asserted afterwards. It is what keeps
  the updater and `Help ▸ What's New` from offering the build (both filter pre-releases out), so a lost flag
  hands a beta to every user. `PRERELEASE=1` or a tag with a pre-release identifier both count.
- Release notes: a hand-written `docs/release-notes/<version>.md` wins — it is the copy that travels inside
  the package — otherwise a description typed in the web UI is **left alone**. Do not restore the
  unconditional `gh release edit --notes-file`; it would overwrite what a human typed with commit subjects.

## §9.2 — Input goes through the unified pipeline
- Keyboard handling flows through `src/Bearing.App/Input/` (`Gesture`/`GestureParser`, `Keymap`,
  `CommandRegistry`/`KeyCommand`, `KeyDispatcher`, `CommandIds`, `KeyScope`). Views call `TryHandle(e, scope)`.
- WHEN adding a shortcut, register a command + default binding in the keymap — do NOT hand-roll a new
  `OnKeyDown` branch. Grid **spatial** navigation (cursor motion) is the deliberate exception and stays local.
- User overrides load from `keybindings.json` (VS Code-style array) via `KeymapLoader`, layered over
  `KeymapDefaults`.

## §9.3a — Connection state is drawn as the beacon, on its own palette
Two palettes, disjoint by design (`design_handoff_bearing_v3/CONNECTION_STATUS.md` rev. 2026-08-27):
- **Environment** — rose / gold / mint, the per-connection hue. Owns **surfaces only**: tab washes, the
  status-bar rule, the env chip, the schema-tree row fill. Mutable via `App.SetConnectionAccent(hex)`.
- **State** — `Status.Connected` / `Status.Connecting` / `Status.Disconnected` in `Themes/Tokens.axaml`.
  Owns **icon strokes and label text only**. NEVER fills a surface, and the environment hue must never
  colour a beacon: a disconnected production session is a red beacon on a rose wash.

`Controls/ConnectionBeacon` is the one mark, used at all four sites (toolbar 14px, status bar 13px, tab
headers 12px, schema-tree server rows 13px). It carries state in the **silhouette** — connected is a filled
core in a closed ring, connecting dashes and pulses that ring, disconnected drops the ring *entirely* and
strikes a hollow core through. That loss of mass is the signal; colour is secondary, which is what fixed the
old chain / broken-chain pair whose states differed by a gap invisible below ~20px.
- WHEN adding a connection-state indicator, use `ConnectionBeacon` — do not draw a new glyph, and do not
  scale it below 12px (core and ring merge).
- Its `State` is a `ConnectionState`, so every VM that feeds one carries the tri-state, not a bool
  (`EditorTabViewModel.ConnectionState`, `SchemaNodeViewModel.ConnectionState`).
- The toolbar toggle is `Icon.Power` — **one glyph in all three states**, coloured by the state palette.
  It is an action, not a second readout; it used to mirror the status glyph and made the toolbar say the
  same thing twice. Do not re-couple its geometry to the state.
- Avalonia's `StrokeDashArray` is in units of `StrokeThickness`, unlike SVG's `stroke-dasharray`, which is
  in user units. The spec's `3 4` at stroke-width 2 is `1.5, 2` here.

**Not yet applied** from that handoff: the tab-strip re-treatment (DELTA item 4) — env wash on every tab,
the 2px environment rule as the editor's top border, `7 7 0 0` radius, `8,12` padding. The per-tab env chip
is already gone (the beacon sits bare), but the strip itself is unchanged.

## §9.3 — Avalonia specifics
- Avalonia is **v12.1**. Drag & drop uses the typed in-process `DataTransfer`/`DataFormat` API
  (`DataObject`/`DoDragDrop` are obsolete → `DoDragDropAsync`).
- Theming: token brushes in `Themes/Tokens.axaml`, dark Kanagawa variant in `App.axaml`; the per-connection
  accent is mutable via `App.SetConnectionAccent(hex)`.

## §9.4 — Two granularities: server links and per-database pools
**A pool is per (connection, database); "connected" is per connection.** Postgres binds a connection to a
database at startup — there is no `USE` — so a pool can only ever be per database. But the user's question is
about the server, and answering it from the pool map made the app contradict itself: the schema tree's
server row lit while the tab beside it on another database read as disconnected, and Connect (one database)
was not the inverse of Disconnect (all of them).

`ConnectionSessionManager._links` is the server link — a `HashSet<Guid>` of connections we completed a
handshake to. **Every user-visible connected/disconnected indicator reads `IsLinked(Guid)`**: the toolbar dot,
the tab-header beacons, the schema tree's server row. Nothing user-facing reads `TryGet`/`IsAnyLive`.
- Set by a successful `BuildAsync`, on **any** database — authenticating against `app` proves the server is
  reachable as well as authenticating against `reporting` would.
- Cleared by: `EvictConnectionAsync` (unconditionally, including when the sweep already emptied the pools —
  otherwise Disconnect would be a no-op in that state), `EvictAsync(SessionKey)` when it removes the
  connection's last live session, a failed connect with nothing else live, a credential nearing expiry, and
  `CloseAllAsync`.
- **NOT cleared by the idle sweep.** Reclaiming pools is not disconnecting: Npgsql has already pruned the idle
  sockets by then, the credential is still cached, and the beacon going dark while the user reads a result set is
  the surprise this model exists to remove.
- `LinkChanged(Guid)` is the coarse event — one gain, one loss, per server. A second database opening on an
  already-linked server fires only `LiveChanged`.
- `ConnectionsViewModel.SetTabDatabase` warms the new database's pool in the background when the server is
  already linked (`WarmDatabaseAsync`), so "connected" is a fact rather than a promise. This is not the
  removed connect-on-tab-switch: the credential is already in memory, so it never prompts and never reaches a
  server the user didn't opt into.

## §9.4a — Sessions are keyed by (connection, database)
`ConnectionSessionManager`'s `_live`, `_inflight`, `_schemaCache` and `_schemaInflight` all key on
`Connections/SessionKey` — connection id **and** database. A pool is bound to one database (it is in the
connection string), so an id-only key made switching database on a tab count as "settings changed" and threw
away a working pool, its TLS handshake, and all its server-side state (#54, fixed 2026-08-22).
- Two tabs on the same server but different databases now have **independent pools**; switching back and
  forth reuses both instead of rebuilding either. Only a real settings change (host/port/user/options) still
  rebuilds — see `SameConnection`, which no longer has a database-switch case to serve.
- Eviction is therefore two operations, and picking the wrong one is a behaviour bug:
  `EvictAsync(SessionKey)` for one database (a cancelled connect, a credential retry) and
  `EvictConnectionAsync(Guid)` for the server (toolbar Disconnect, connection edited/deleted/refreshed,
  project close). Server-level actions must use the second: it is what drops the server link (§9.4), and a
  one-database evict leaves the beacon lit everywhere the user can see it.
- `LiveChanged` carries the `SessionKey`, not the id — one connection can have several live sessions, so
  "connection X changed" would not say which pool moved.
- `NpgsqlConnectionFactory` caps `MaxPoolSize` at 10 because there is now a pool per database rather than
  per connection; Npgsql's default 100 would have been an N x 100 ceiling. `ConnectionInfo.Options` can
  still override it.

## §9.5 — antlr4-c3 completion
- The vendored `antlr4-c3` `CodeCompletionCore` is used for SQL completion. There is a known gotcha noted in
  project memory — verify completion behavior against the existing `CompletionEngine` tests when touching it.

## §9.7 — Demo mode is a whole session, decided once
`--demo` / `BEARING_DEMO` (or the Connections empty state's "Explore demo data") starts a session served from
`Bearing.Demo`'s fixed catalog with no database anywhere (#64). It is **process-wide**: the demo registry holds
`DemoProvider` *instead of* Postgres, so the fake is not reachable from any normal connection flow — in an
ordinary session it is not in the object graph at all.

- WHEN adding to it, keep the isolation. `App.DemoWorkspace` points the project store, session store, query
  log and recent-projects list at one temp directory and deletes it on close, and hands the session a
  `NoSecretStore`. Nothing may touch `BearingPaths.DataDir`/`ConfigDir`: no manifest write in the user's
  project, no recent-projects entry, no query-log rows beside their history (§1.3), no keychain call (§1.1).
- The demo connection keeps `RequireWriteConfirmation` — §1.2 says the write guard is not special-cased, and
  the confirmation is a feature worth demonstrating. It is labelled through the existing environment
  mechanism (§9.3a), not a new mark.
- It ships in **Release**, not compiled out: evaluation by someone with no Postgres is the strongest argument
  for the feature, and a build without it cannot serve that. Safety comes from the isolation above.
- The empty-state button **relaunches** rather than switching, and leaves the current window open — demo mode
  is decided at startup, and swapping the registry and every store under a live session is how you get a
  window that is half demo and half real.
- `DemoWorkspace.DisposeAsync` closes the query log *first*. `SqliteQueryLog.DisposeAsync` now also releases
  its own connection pool, because reads hand connections back to it and the handle otherwise outlives the
  object — a demo that cannot delete its directory leaves exactly the residue it exists not to leave.

## §9.8 — A late tree read that *adds* rows starts in `OnChildrenAttached`, not `LoadChildrenAsync`
`SchemaNodeViewModel.EnsureChildrenAsync` clears `Children` **after** `LoadChildrenAsync` returns, so
anything appended in the meantime is thrown away — a race whose outcome depended on whether the catalog
answered faster than the tree rebuilt. `OnChildrenAttached` is called once the children are attached and is
where every deferred read that appends a row belongs.
- A read that only **relabels** existing rows was always immune and stays where it was
  (`FillSizesAsync`, #76).
- `DatabaseNodeViewModel` starts sizes + `FillObjectKindsAsync` there; `ServerNodeViewModel` starts database
  sizes + `FillRolesAsync`.
- Both raise an internal event (`SizesLoaded`, `ObjectKindsLoaded`, `RolesLoaded`) because nothing in the app
  waits on these — which is the point of loading them late, and the only way a test can.

## §9.9 — The explorer's object kinds, and where each one is shown (#119 / #120)
`IMetadataReader.GetDatabaseObjectsAsync` returns `DatabaseObjectKinds` — sequences, user types, extensions,
policies — in **one** call rather than four: they are all wanted at the same moment, and a new engine's cost
stays one method. Each kind's read is best-effort **on its own** (`Best`), so a role that cannot read
`pg_policy` still gets its sequences.
- It is a **late** read (§9.8), not a field on `DatabaseObjects`: `GetObjectsAsync` is awaited before the tree
  renders, so four more catalog reads there would make every expand slower by all of them.
- **Policies appear twice, from two reads.** Per table via `TableDetails.Policies` (beside constraints and
  triggers — where you are standing when a row count surprises you) and per database in the Policies group.
  Different granularities, like relation sizes versus database sizes.
- **Procedures are their own group.** `RoutineKind` always distinguished them and the tree did not; `CALL`
  versus `SELECT` is a difference you need at the point of use. Aggregates and window functions stay with the
  functions — the split is about how you invoke the thing.
- Postgres specifics worth not rediscovering: the composite type Postgres creates per table/view is excluded
  by `relkind = 'c'`, or every relation would be listed twice under Types; sequence ownership needs
  `pg_depend.deptype in ('a','i')` ('i' is an identity column) — and **pagila's own sequences have no
  `OWNED BY` at all**, so that join cannot be asserted against them; a sequence's type is its own (`bigint`
  by default), not its column's; `last_value` is null for a sequence never read from, which is not zero.
- **Roles hang off the server node**, never a database: they are cluster-wide. Grants are per database, and
  the group label says which one.
- A null-valued *untyped* parameter gives Postgres nothing to infer a placeholder's type from, and the read
  fails — silently, when `Best` turns a failed kind into an empty list. Append the optional predicate to the
  SQL instead of writing `$1 is null or …`.

## §9.9a — The rest of the explorer's breadth, and where each thing lives
The follow-up to §9.9 closed the gaps #119 left. Placement is the whole design here, and each choice has a
reason that is not "it was convenient":

- **Column defaults, identity, generated expressions, collations and comments** are in `TableDetails`, not
  `ColumnInfo`. `ColumnInfo` lives in the snapshot — the completion hot path, loaded in bulk for every column
  of every table — so a default expression and a comment per column would inflate the one structure whose
  point is being cheap. The detail read is now awaited **before** the column nodes are built, and its failure
  path still yields the columns with their types (they come from the snapshot; only the extras need the trip).
- **A generated column never also reports a default.** Both arrive in the same `adbin`, and a default the
  column can never use would be a lie about how it is written. Identity outranks a default on the row for the
  same reason.
- **Only a non-default collation is reported** (`attcollation <> typcollation`): every text column has one,
  and listing the database default on all of them buries the single column that was given a different one.
- **Comments** (`obj_description` / `col_description`) reach relations, columns, sequences, types,
  extensions and routines. All of them go through `RelationDetailText.WithComment`, so "where does a comment
  go" has one answer.
- **Partitions are children, not siblings.** `TableInfo.PartitionOf` is the one relation extra that *is* in
  the snapshot, because the tree needs it to arrange the list before anything is expanded, and it is one
  nullable long per relation. A partition whose parent is **not** in the snapshot still appears at the top
  level — hiding it would make it vanish. `pg_inherits` covers declarative partitioning and legacy
  `INHERITS` alike, and the parent's row carries the count.
- **Rules** are per-table, with `_RETURN` excluded: on a view that rule *is* the view, and reporting it would
  make every view look like it had a rule.
- **A Schemas level is additive**, not a restructure. The inline list stays the primary view — it is what
  nearly every expand is for and it is already ordered by `search_path` rank — and the Schemas group adds the
  level the tree could not express. Objects therefore appear in both places, the same call the policies made.
  Skipped entirely for a single-schema database, and **lazy**: its children are a second set of nodes for
  relations the inline list already holds, so a database with two thousand of them must not pay up front.
  Inside a schema row, children are named unqualified.
- **Tablespaces sit on the server** beside Roles — `pg_tablespace` and `pg_subscription` are the only
  `relisshared` catalogs here. Event triggers, publications and foreign servers are **per database**, despite
  reading as cluster-wide.
- **One record for the long tail.** `SchemaObjectInfo` (id, schema, name, detail, comment) serves
  publications, subscriptions, foreign servers, event triggers, collations, casts, operators, operator
  classes, text-search configurations and tablespaces, with one `SchemaObjectNodeViewModel` and one query
  each. The same call `TypeInfo.Detail` already made: these answer with a string the user reads and nothing
  computes against, so a shape per kind would be twelve near-identical records carrying no extra information.
  A kind graduates to its own record when something starts *using* a field, as `PolicyInfo.TableId` does.
- `DatabaseObjectKinds` is **init-properties with empty defaults**, not positional: there are a dozen of
  them, and a caller that cares about one should not have to name eleven.

## §9.9b — Six defects a code review found in §9.9/§9.9a, and the shape of each
Worth keeping because each is a *class* of mistake this tree invites, not a typo:

1. **A late append needs a generation guard.** `OnChildrenAttached`'s reads finish after an await, and
   "Refresh metadata" clears `Children` and re-runs the hook on the *same* node — so an in-flight read
   landed its group on the rebuilt children and the new read added a second. `LoadGeneration` /
   `IsCurrentLoad` is captured before the await and checked after. The window is the length of a catalog
   query. The size read needs it too: it reorders live `Children`.
2. **A hierarchy has to be passed down, not sliced.** Handing each nested relation node only its own child
   list left a *sub*-partition reachable from nowhere: skipped at the top level (its parent was present) and
   never added below. `PartitionMap` travels whole, and it is indexed — the first version scanned
   `Tables.Any(p => p.Id == parent)` per partition on the path the tree renders from.
3. **A `left join` on a one-to-many catalog fans out.** `pg_inherits` has a row per (child, parent) pair and
   classic `INHERITS (a, b)` is legal, so the relation query emitted the same table twice — two tree rows,
   two completion entries, two picker entries. Read the parent as a scalar subquery that reports one only
   when there is exactly one.
4. **A subscription to a model outlives the visual that made it.** `ColumnLayout` lives as long as its
   `ResultSetViewModel`; a grid lives until the next render, and `RebuildResults` runs on **every tab
   switch** over the same result sets. The handler is tracked in `_layoutSubscriptions` and detached at the
   top of `Rebuild`, or each switch leaks a closure pinning a discarded `DataGrid` and its whole visual tree.
   `ColumnLayout.ChangedSubscribers` exists so that is assertable.
5. **An invariant enforced at write time is not enforced retroactively.** `GridSelectionOps` keeps a hidden
   column out of a *new* selection, which made "a selection copy is what you see" true by construction — and
   false for a selection made before the hide. `GridSelectionController.DropHiddenColumns` prunes on change,
   in the controller because it is the only writer of the model.
6. **Two coordinate spaces need naming.** "Freeze through here" is a count of leading **visible** columns in
   display order; a hidden column keeps its `DisplayIndex`, so counting raw display indexes while
   `ColumnLayout.Freeze` clamps against the visible count froze fewer columns than the menu item promised.
   The arithmetic is `ResultColumnMenu.FreezeThrough`, next to the item it serves.

## §9.10 — Freezing and hiding columns: hidden means unselectable, not unexported (#118)
`ResultSetViewModel.ColumnLayout` (pure, `Results/ColumnLayout.cs`) holds the hidden set and the frozen count
per **result set** — it describes what is on screen, and a re-run is a different result. The grid reads it
in place (`ResultView.ApplyColumnLayout`); a layout change never rebuilds the grid, which would drop the
scroll position and any pending edits.
- Two rules keep the user out of a corner: the **last visible** column cannot be hidden (a grid with no
  columns has no header left to undo from), and freezing is clamped so at least one column stays scrollable.
  Hiding narrows an existing freeze.
- A hidden column is excluded from **selection** everywhere — `GridSelectionOps.FirstColumn`, `LastColumn`,
  `StepColumn`, `Rectangle`, `AllCells`. One rule at the four places that decide where columns are, which is
  what makes "a selection copy is what you see" true by construction and keeps an arrow key from parking the
  cursor on a cell with no visual.
- **Exports and full copies still carry every column** — and the meta row's "2 columns hidden" marker
  (`HiddenColumnsText`) is what keeps that from being a surprise. A hidden column may be an omission; it must
  never be a silent one.
- The **column header menu** (`ResultColumnMenu`) is resolved from `ContextRequested` in the tunnel phase,
  because the grid would otherwise answer a header press with the cell menu. It is the shared home for
  #106's sort.

## §9.10a — Focus arriving in a results grid is not a viewport event (#60)
`GridSelectionController.SeedActive` seeds a cursor when the grid takes focus with none — and it used to
seed the result's **absolute** first cell and scroll to it. That ran from the grid's `GotFocus`, which the
click that focuses the grid raises *before* the click's own selection is applied, so a horizontally scrolled
result snapped back to column zero on the first click into it: a cell, a column header, the row gutter, the
corner, all four. Only the cell path had a corrective (`ResultCellFactory.KeepClickedCellInView`), and it
scrolls the clicked cell in *minimally* — a no-op when that cell is already visible at offset zero, which is
the case the user sees.

- It only fires on the **first** click into a grid that does not already hold the cursor (`NeedsSeed`), which
  is why it read as intermittent: the second click is always clean.
- The seed now names the first cell **already on screen** (`FirstCellInView`) and passes `scroll: false`.
  Seeding is not navigation — it says where the cursor is, it does not move it. `MoveActive` still scrolls by
  default, because a cursor the keyboard moved has to stay followable.
- A column scrolled off to the left keeps its cells realized but **collapses them to nothing and parks them
  at a positive x**, so a position test alone picks the leftmost column of the *result* rather than of the
  viewport. `FirstCellInView` filters on `IsEffectivelyVisible` and a non-zero size for that reason
  (measured, §4.5).
- `SelectBand` and the corner branch take focus **after** writing the model, so the seed does not run at all
  on a header press. Keep that order.
- **A press is never a scroll.** `KeepClickedCellInView` is gone: once the seed stopped jumping, all it
  still did was `ScrollIntoView` a cell clipped by the viewport edge, so clicking the sliver of a
  half-visible column slid the result sideways. Nothing the user clicks needs revealing — they could see it
  well enough to click it. Cursor *motion* still scrolls (`MoveActive` with `scroll: true`); that is the
  user moving the cursor, not the pane moving itself.
- **Both of that corrective's documented causes are now measured away.** The second was the quick-stats bar
  re-measuring the grid, which is why it was posted at `Loaded` priority. The bar does cost ~30px of grid
  height when it appears — and changes *neither* scroll offset, so the pressed cell does not move. What
  remains is occlusion, not movement: a cell in the bottom 30px ends up under the bar. Holding still is the
  better of the two.
- **A chrome press has to seed the cursor in view, not just leave the viewport alone.** A band's origin is
  the result's first row (column header) or first column (row gutter), and while focusing scrolled to the
  top-left that origin was on screen by accident. With the viewport correctly held still it is not, and the
  first arrow key afterwards yanked the grid from offset 386 to 27 — the same jump, one keystroke later.
  `SelectBand` therefore takes a `cursor` separate from its `origin`: the origin stays the **anchor**, so a
  later Shift+click still extends from where the band starts, and only the cursor is pulled into view
  (`InViewRow` / `InViewColumn`).
- **A commit does not reveal either.** The post-commit `ScrollIntoView` in `WireEditing` was the twin of
  `KeepClickedCellInView` and went the same way: the causes it was written for (the grid re-adopting a
  current cell, the row re-tinting, the cell's content swapping) do not move the viewport, and what it still
  did was reveal a clipped cell — so committing an edit in the sliver of a half-visible column slid the
  result 304 → 250, the same jump, from a commit instead of a click.
- **One Tab is one scroll.** Avalonia's `DataGrid_KeyUp` answers a Tab *release* with
  `ScrollSlotIntoView(…, forceHorizontalScroll: true)` aimed at the DataGrid's **own** current cell — which
  is not our cursor (we own cell selection, §9.2; only a click and `BeginEdit` ever set the grid's), so it is
  wherever the last click left it. Tabbing scrolled twice per keystroke and disagreed with itself: 304 → 153
  on the key down, then → 56 on the release. `WireSelection` claims Tab's **KeyUp** in the tunnel phase, as
  it already claims the KeyDown. A cell editor's Tab is left alone (`e.Source is not TextBox`).
  - `HeadlessWindowExtensions.KeyPress` is key-**down** only. That is why no test could see this: the
    release has to be sent with `KeyRelease` explicitly.
- **`BeginEditActive` is the one keystroke still allowed to scroll, and it must stay that way.**
  `grid.BeginEdit()` needs a *realized* cell to put an editor in: with the cursor scrolled out of view and
  that `ScrollIntoView` removed, F2 opens no editor at all and reports nothing — measured both ways.
  Revealing the cell you asked to edit is part of executing the command. The cursor can be off screen at all
  only because the wheel and the scrollbar move the viewport without moving it; every other route keeps the
  two together.
- Two traps for the next test here:
  - a fixture that scrolls with `ScrollIntoView(row, col)` and then clicks *that same cell* restores the
    identical offset by accident and passes over the bug — click a different cell;
  - `WideEditableResult`'s only numeric column is its primary key, and `GridSelectionOps.MeasureValues`
    excludes PK **and** FK columns, so no selection in it can ever raise the stats bar. A test that thought
    it was measuring the bar was measuring nothing; `WideNumericResult` exists for that.

## §9.10b — Selecting a tree row must not scroll the panel sideways (#60, same pass)
`TreeView` calls `BringIntoView()` on the row container on every selection change, and that overload asks
for the container's **whole width** — a deep indent plus a long qualified name, wider than the panel. The
scroll presenter honoured it, so clicking a group row like *Views* threw the tree sideways to chase the end
of a row the user had just successfully clicked. `TreeChrome` rewrites the request instead.

- **The rewrite is a zero-width sliver at an x already inside the viewport**, which leaves the request
  purely vertical. Zeroing the *width* alone is not enough and was the first attempt: the rect keeps the
  row's own left edge as its x, and in a panel already scrolled right that edge is off-screen to the
  **left**, so the presenter still acted — by scrolling back to zero. Clamping x into the visible range is
  what makes it a no-op in both directions.
- **Vertical bring-into-view is deliberately kept.** "Go to table" / F12 (§9.11) reveals a row by scrolling
  to it, and a fix that killed bring-into-view outright would have broken that silently.
- **It is a class handler on `TreeViewItem`, and it must run *after* Avalonia's own.** The event is
  bubble-only and the presenter that acts on it is an *ancestor* of the row, so a handler on the `TreeView`
  runs after the scroll has already happened. But `TreeViewItem` registers its own class handler for the
  same event, which rewrites `TargetRect` to the header's bounds — whichever registers second wins, and
  that was decided by which type's static initialiser ran first. **It silently reversed between two runs of
  the suite.** `RuntimeHelpers.RunClassConstructor(typeof(TreeViewItem).TypeHandle)` in the static
  initialiser makes the order ours to state; the header rect is the better input anyway.
- **Scoped, despite being a class handler**: the row's tree must be one `Apply` was called on (a weak table),
  or "is the trim in force" would depend on whether anything had touched `TreeChrome` yet — the test-order
  trap §4.5 documents for `ThemeBrush.AtAlphaCached`. And the request's target must be the row container
  *itself*, so a descendant asking to be revealed on its own behalf (an inline-rename box scrolling to its
  caret) is left alone.
- The trap for the next test: **both of the first two tests started at offset zero and could not see the
  bug.** A tree-scroll fixture has to set a non-zero offset, and its click has to land inside the viewport —
  aiming at the row's own left edge misses the panel entirely once it is scrolled, and then passes while
  hitting nothing.

## §9.11 — Go to table / go to definition (#117)
Two halves, split at the testability seam:
- `Bearing.Sql.GoToDefinition.Resolve(sql, caret, snapshot)` is pure. Scoped to the statement under the
  caret, like execution. **Which part of a dotted chain the caret is on decides the answer**: on `p` in
  `p.amount` the user is asking what `p` is (the table); on `amount`, the column. It resolves an alias
  through `FromClauseExtractor` — *not* `AliasResolver`, which generates a new alias — and returns null
  rather than guessing: a jump to the wrong table is silent, and the user then reads the wrong schema.
- `ConnectionsViewModel.RevealRelationAsync` does the descent, which cannot be pure: schema nodes load
  children on first expand, so the path to a table does not exist until three awaited reads have happened.
  The matching at each level is `Workspace/SchemaTreeReveal`'s. It returns a named `SchemaRevealResult`
  rather than a bool — each outcome is a different sentence for the status bar.
- A relation is matched on its **schema and name**, never its `Title`: a row in the default schema is
  labelled `payment` and one elsewhere `reporting.payment`.
- `WorkspaceContext` takes an optional `ISchemaBrowser` so this is testable without a live server (§2.4).

## §9.12 — The tab strip: three routes to a tab, and a drag to reorder
**The `▾ N` button is a dropdown of every open tab** — Visual Studio's document-well dropdown, in the same
place: press it, point at a tab, go there. `Controls/TabOverflowMenu` fills it; it is attached as the
button's own `Flyout`, the shape `ResultExportButton` already used here, so Avalonia owns the open/close.

- **Fill it before the open, never from `Opening`.** The presenter is created and measured around the open,
  so items added inside that window are not in what it draws: the menu reported `IsOpen` with all 27 items
  present and painted an **empty popup**. It is filled from the button's `PointerPressed` (tunnel, ahead of
  the button's own handling), and kept current the rest of the time by `Refresh()` — which the window calls
  when the tab rows or the selection change. Both halves are needed: the keyboard opens this too (Space on
  the focused button raises no press), and the fill in the constructor finds no workspace, because the
  window has no `DataContext` yet.
- `TabStripOverflow` is therefore built **after** `_dispatcher`: it is handed `MenuGesture`, which reads the
  live keymap through that field, and filling the menu before it is assigned would throw.
- `Toggle` refreshes the menu **posted**, not inline — it is reached *from* the expand item's own click, and
  refilling there clears the presenter's items while the item that raised the click is still dismissing.
- A flyout test that asserts `IsOpen` and an item count passes through all of this. The one that does not is
  `The_dropdown_renders_its_items_not_just_an_empty_popup`, which counts realized `MenuItem`s inside the
  open popup (§4.3: a property is not a rendered thing).
- The pinned tabs are listed first, behind a separator — the two groups the strip draws as two rows.
- The active tab is marked with a tick in the **icon column**, not in the header text, so the marks line up
  down the list. A name over 44 chars is trimmed and its dirty dot survives the trim; the full path is the
  item's tooltip, as on the tab itself.
- The `go` callback checks the tab is still in `Tabs`. A dropdown can outlive its list — a background close
  (a deleted script, a project switch) can take a tab away while the menu is open.

**Each of the three routes answers a different question**, which is why all three exist and why the other
two hang off the bottom of the dropdown rather than being their own buttons:
- the dropdown — "which tabs do I have open", pointed at, where the tabs are;
- the modal picker (**Ctrl+E**, `tab.pick`) — "where is the one called X": it filters, and it carries each
  tab's folder, which is what tells two same-named scripts apart;
- **expanding the strip** (`tab.expandStrip`, unbound) — "show me all of them at once", and leaves them on
  screen to be dragged around.

**Expanding** swaps both rows' `HorizontalScrollBarVisibility` from `Hidden` to **`Disabled`** — that is what
constrains the strip to the viewport, which is what makes its `WrapPanel` wrap — and takes a `MaxHeight`
with a vertical scrollbar past it. State lives in `Views/TabStripOverflow`.
- The cap is **per row and not the same for both**: about five rows for the strip, two for the pinned row.
  One shared constant was applied to each of them, so a full pinned row above a full unpinned one could
  take twice the height the constant claimed — and pinning is for the handful of scripts you keep, so its
  row is the one that does not need the room.
- The `ItemsPanel` is an explicit `WrapPanel` on both strips. Measured at infinite width (collapsed, inside
  the scroller) it is a `StackPanel` in every observable way; the expand is entirely "which of the two
  measurements the scroller asks for".
- Collapsing scrolls the selection back into view — it is often one of the tabs that were off the edge.

**The button is always visible now**, which reverses #65's rule and does not contradict its reason. What was
forbidden was a `»` chevron *claiming tabs were hidden* when none were — the same false claim that retired
the scrollbar before it. A list of the open tabs is never that claim, so the caret stands alone and the
count decorates it only when there is one to report (`TabOverflow.Chevron`).
- The three safeguards against the #65 layout loop are unchanged and still all three: the width is reserved
  from the count whether the button shows or not, the button's width is fixed at that same 40px, and `Sync`
  writes nothing unless something changed.

**And a tab drags to a new position, or onto the other row to pin or unpin it** (`Views/TabDragReorder`).
- **Pointer capture, not `DragDrop.DoDragDropAsync`** — which is the right call for the trees, and the wrong
  one here. A platform drag session grabs the pointer and runs its own modal loop, so it cannot be driven
  from synthetic input; capture can, and `tests/Bearing.App.Tests/Ui/TabDragTests.cs` drives the real
  gesture end to end because of it (§4.5).
- **Nothing moves until the pointer is released.** A mis-aimed drag is therefore free, and no reorder
  happens while the container bounds the drop is read from are still moving.
- **The drag handle is the label, not the whole tab.** The pin toggle and the ✕ own their presses (the pin
  toggles on the press itself), and on a ~100px tab they cover the right-hand third — a test that pressed
  the middle landed on the pin and asserted nothing. Same rule as a browser, whose ✕ is not a drag handle.
- **The drop reads the pointer's row, then its X.** The expanded strip wraps, so row two repeats row one's
  X range; X alone files every drop on it into row one. `TabReorder.Hit` does the row-then-column part, and
  a row the pointer is *in* beats one it merely touches: a `WrapPanel` puts row two's top exactly on row
  one's bottom, and an inclusive test matched both on that line.
- **A release outside the strip moves nothing.** "The nearest row, always" made every release a drop —
  a tab dragged straight down into the editor was filed at the *end* of the strip, from a gesture that never
  moved sideways. `DropTolerance` (48px) is the ceiling; inside it the nearest row still wins, because a row
  is under 30px tall and a caret that blinks out whenever the hand wobbles reads as broken.
- **Escape abandons a drag**, on the window in the tunnel phase ahead of the window's own Escape (which
  cancels a running query), and it marks the event handled so that one does not also fire. This is the
  thing a platform drag session would have given for free; not having it, with every release counting as a
  drop, left no way at all to abort.
- **Edge auto-scroll is a timer**, not a step per pointer-move: a pointer held still in the edge zone is how
  you ask for a long scroll, and one step per move meant the only way to travel was to jiggle the mouse.
  The row and direction are fields the tick reads, because a handler subscribed per move cannot be
  unsubscribed — each closure is a different delegate — and the timer ends up with one handler per move.
- **A slot in a row is not an index in `Tabs`.** The rows are two views over one master list, so a slot has
  to be translated through every tab's `IsPinned` — `TabReorder.TargetIndex`, in
  `ObservableCollection.Move`'s destination space (the list with the moved item already removed). Mixing
  those two spaces is §9.9b item 6 again.
- `WorkspaceViewModel.MoveTab` is the one mutation, reorder and re-pin together, because on the strip they
  are one gesture. `SetPinned` stays for the keystroke and the menu, which have no position to offer. A
  re-pin that moves nothing still has to `ResplitTabs` itself — there is no `Move` to raise the collection
  change that normally does it.
- The order it writes is the order `session.json` saves, so a rearranged strip comes back rearranged.
- `Nearest` picks the row **closest** to the pointer rather than the one under it: a row is under 30px tall,
  and a caret that blinks out whenever the hand strays above or below reads as a broken gesture. Crossing
  rows still takes moving onto the other one.

## §9.13 — The activity panel is the only thing that polls a server, and it is the only thing that acts on one (#101)

`pg_stat_activity`, refreshed every 2.5 s while the panel is on screen, with Cancel and Terminate on a row.
It exists to close the loop 0.5.3 opened: a query that outran a timeout is told it "may still be running on
the server", and there was no way to look.

- **It is the first code in the app that executes a side-effecting Postgres function.** The standing
  precedent is the opposite: `SequenceNodeViewModel.SetvalSql` only *copies* `setval(…)` to the clipboard so
  it goes through the editor and the write guard, and §1.7 refuses to generate `GRANT` at all. That precedent
  does not transfer here — a panel that copied `select pg_cancel_backend(12345)` to the clipboard would not
  answer "my query is hung", which is the entire feature. It executes, and carries the confirmation instead.
- **`IServerActivity` is a seam of its own, not three more methods on `IMetadataReader`.** That interface has
  no mutating member — it is read-only by construction — and a reader that can end someone's session is a
  different thing. The name is the point.
- **Every action confirms, including on our own backends**, and the dialog's `IsCancel` button is also
  `IsDefault` (the `ConfirmDeleteScriptDialog` treatment), so no single keystroke kills a session.
  `IDialogService.ConfirmBackendActionAsync` defaults to **false** with no window — the delete side of that
  line, not `ConfirmWriteAsync`'s.
- **Terminate is refused on a read-only connection; Cancel is not.** The asymmetry is the decision. Neither
  is blocked by #99 on its own — `pg_terminate_backend` is a function call, not a transactional write, so
  `default_transaction_read_only` lets it straight through and the refusal has to be ours
  (`WriteRefusal.ReasonForTerminate`). Cancelling stops a statement and leaves the session, and it is most
  wanted on exactly the production connection most likely to be read-only.
- **Polling starts and stops from `ShellViewModel.SyncPanelActivity`, hung off both `OnActivePanelChanged`
  and `OnSidePaneOpenChanged`** — the same pair `RefreshHistoryIfShowing` uses, and for the same reason:
  `[ObservableProperty]`'s setter short-circuits on an unchanged value, so collapsing the pane from the
  Activity tile and re-opening it never changes `ActivePanel`. Hung off that one alone, the panel comes back
  visible and frozen. There is a headless test for exactly that sequence.
- **The read never connects and never blocks**: `Sessions.TryGet` (§1.5's rule — a panel refreshing itself
  must not raise a credential prompt), a `SessionLease` taken **per read and released**, and a per-read
  deadline under the interval so a slow server costs refreshes rather than queueing them. A tick arriving
  while a read is in flight is skipped, not queued.
- **An open panel does keep the pool warm, and that is correct.** The per-read lease is not what stops that —
  `Lease` and `ReleaseLease` both stamp `LastUsedUtc`, so a session read every 2.5 s never goes idle. Sweeping
  a pool that is being queried continuously would only make the next tick rebuild it. What the short lease
  buys is that a *retired* session can still finish dying: a lease outstanding across ticks would block an
  evict or a database switch from completing. The bound on the warmth is that polling stops the moment the
  panel leaves the screen.
- **An action re-resolves the session after its confirmation, not before.** A modal dialog can sit open for
  minutes, and `Lease` does no validity check — so the idle sweep, a Disconnect or a connection edit can
  dispose the session captured when the menu was opened, and the confirmed action then surfaces as an
  `ObjectDisposedException` rather than doing anything. `SaveChangesAsync` takes its lease after its
  confirmation for the same reason.
- **A failed read keeps the rows it had.** The list refreshes under a pointer that may be on its way to a
  row; clearing it on a blip is how a user terminates the wrong backend.
- **Row identity is pid + `backend_start`.** A pid alone is reused by the server, and the selection drives
  which backend the menu acts on.
- **It lists one database, not the whole cluster, unless asked.** `pg_stat_activity` is cluster-wide, so an
  unfiltered read returns every session on the host — other databases, other roles, other applications, and
  the statement each is running. The default is the connection's own database; "Every database on this
  server" widens it, which is what you want when hunting a lock holder and not what a panel opened beside
  your own work should show unasked. The status line names the scope, because "2 sessions" is a different
  claim about a database than about a server.
- **Plainly idle backends are left out by default, and `idle in transaction` is never treated as idle.** On a
  production database most sessions are an application server's pool between statements: nothing on them to
  cancel, and enough of them to bury the handful doing something. `idle in transaction` holds locks and is the
  row the panel most exists to show, so the filter is `state is distinct from 'idle'` — `is distinct from`,
  because a state the role may not read is not thereby idle, and hiding it would assert something nobody
  checked. The status line says "running" when the filter is on, since a count that quietly dropped rows is a
  different number.
- **The narrowing is an appended predicate, not `($1 is null or datname = $1)`.** §9.9's trap: a null-valued
  untyped placeholder gives Postgres nothing to infer a type from and the read fails.
- **Another session's statement is shown, formatted and syntax-coloured**, through the same read-only viewer
  the history preview uses. It is the reason you would cancel a backend, so it should read as well here as in
  the editor — and what the server returns is whatever the client sent, which for an ORM is one long line.
  Formatted asynchronously (the formatter parses) with the raw text up first, so the pane is never blank and
  a statement the formatter refuses simply stays as it arrived.
- **The panel's own poll is never in the query log**, by construction: logging lives in
  `ExecutionViewModel.LogExecution` and this does not go through it. The two *actions* are also not logged —
  §1.3's log is the record of SQL the user ran, and a terminate is not a statement they typed. Recording
  administrative acts against production is a real and separate question; it wants its own column rather than
  a fake SQL row, and #113's report is where it would belong.
- It is a control of its own (`Controls/ActivityPanelView`), not a fourth `DockPanel` in `SidebarView` —
  §0.4/§9.1, and that file is already ~925 lines.
- **The issue's column table does not fit.** A side panel is 262px and `pg_stat_activity` offers eight
  columns, so the list carries the two lines you scan (pid · state · duration, then the statement) and the
  rest goes to the detail pane under the splitter — the trade the History panel already makes.

### §9.13a — A restricted role does not see other sessions *at all*

Measured against PostgreSQL 18, because the plan had assumed otherwise and it changed the design: a role
without `pg_read_all_stats` does **not** receive other sessions as rows with the detail columns blanked out.
It does not receive the rows. At one moment, the superuser's read returned three client backends and the
non-superuser's returned one.

So nothing in the result set can reveal the absence — no count of hidden rows, no per-row flag — and a short
list is indistinguishable from a quiet server. `ServerActivity.SeesAllSessions` is asked for separately
(`current_setting('is_superuser')::bool or pg_has_role(current_user, 'pg_read_all_stats', 'member')`, two
branches because a superuser is not a member of anything), and the panel's status line says which answer it
is giving. This is §1.7's `RoleGrants.Visible` in a new place: "this server has one session" and "you are not
allowed to see the others" are different answers.

WHEN adding a read whose result depends on the connected role's privileges, establish what the server
actually withholds before designing around it (§4.7, and this is the fourth instance).

## §9.14 — A database node has two shapes, and one place that decides either (#132)

The tree had grown to seventeen sibling groups under a database, a kind at a time across #119 and its
follow-up. Each placement was right on its own (§9.9a); the sum was not. `AppSettings.SchemaTreeMode` now
picks between:

- **Simple** (the default) — today's shape: relations inline, Schemas / Views / Functions / Procedures beside
  them, and the whole long tail behind one `Other objects` bucket.
- **Full** — schema-first: `Schemas` → a schema → `Tables`, `Views`, `Functions`, `Procedures` and the
  per-schema kinds, with the database-wide kinds in `Administer`.

**Simple is the default, and that reverses the spec.** `SCHEMA_TREE.md` §1 says Full is; #132's own argument
is only that the one-click-to-a-table view must still *exist*, and defaulting to Full would move relations
three clicks deep for everyone on upgrade. What ships is additive.

- **`Arrange()` is the only place either shape is built**, from `_snapshot` / `_routines` / `_kinds` /
  `_sizes` the node already holds. A toggle re-arranges and **re-reads nothing** — a display preference must
  not cost a round trip per database, and re-reading would race the late reads (§9.8).
- **The mode is read live** (`Func<SchemaTreeMode>`), never captured. Storing it meant every construction
  site had to remember to push the current value; a node built after a toggle was then silently stale.
- **Expansion below the database row is not preserved across a toggle.** The two shapes are different nodes,
  and pretending a relation three levels down is "the same row" as one inline is how §9.8's race returns.
- **The kinds landing is handled differently per mode, and it has to be.** Simple *appends* the one bucket,
  which keeps it non-disruptive. Full *re-arranges*, because its kind groups live inside each schema rather
  than under the database — and a single-schema database has no schema row to refresh, so appending left
  those groups permanently missing. Found by a test, not by reading.
- **A single-schema database collapses the schema level in Full mode** rather than skipping it as Simple
  does. Simple can skip because the relations are already inline; in Full the level is the only route to
  them, so skipping would render a database with nothing in it.
- **`SchemaTreeShape` holds the decisions** — which kinds are per-schema versus `Administer`, a schema's
  group order, which schemas exist, and how a count renders — pure and testable without a browser or a
  window (§2.5). The per-schema/Administer split is not invented: `SchemaObjectInfo.Schema` is empty for a
  kind that has none.
- **A schema list now counts the kinds too.** A schema holding only sequences was invisible while the level
  was merely additive; in Full mode it would be unreachable.

### §9.14a — Relations three levels down broke two one-level flattens

Both `DatabaseNodeViewModel.Relations` (which the size read relabels) and `SchemaTreeReveal.RelationsUnder`
(which "Go to table" / F12 walks, §9.11) flattened exactly one group level. That is enough for Simple's
buckets and wrong for Full's `Schemas → schema → Tables → relation`. Both are recursive now, and both stop
at what is **already materialised** — a schema folder loads on first expand, so descending into an unopened
one would build rows nobody asked for.

**And the deeper consequence: a size push has nothing to label.** In Full mode the relation rows under an
unopened schema do not exist when the size read lands, so `FillSizesAsync` keeps the sizes
(`_sizes`) as well as applying them, and rows ask for their own as they are built (`ApplyCachedSizes`). A
one-shot pass would have left every relation under a later-opened schema unlabelled — and silently, since
a missing size looks like a size that has not arrived.

`RevealRelationAsync` therefore expands the named schema on the way down, and only that one: opening every
schema to find one table would be a read per schema.

### §9.14b — A count of nothing is an em-dash

`SchemaGroupNodeViewModel` takes an explicit count now. A group whose read **landed and was empty** shows
`—`; a group whose read has **not landed** is not built at all. So the mark only ever means "asked, and
there are none" (§1.7, absences are typed). The one exception is a bucket standing for nothing whatever —
an entirely empty long tail, or an `Administer` with no kinds — which is omitted rather than rendered as a
row that says so, because that is the empty group #132 asks us not to draw.

The `Other objects` bucket counts **the objects inside it**, not its thirteen sub-groups: "Other objects 14"
is the number that says whether opening it is worth it.
