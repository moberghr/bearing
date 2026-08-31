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
