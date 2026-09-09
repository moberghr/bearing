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
