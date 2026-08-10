# Background execution plan (Stage E)

Status: **DONE 2026-08-09** — all five work items shipped; see `docs/ROADMAP.md` ▸ Stage E for what
actually landed and where it departed from this plan. The toast and the two confirm dialogs are Wayland-
blocked and need eyeball QA (§4.3). Kept as the design record.

Two deliberate departures from the plan below:
- **Item 1** did not stop disposing sessions on a project switch. `CloseAllAsync` became *lease-aware*
  instead, so idle connections are still reclaimed immediately and only the in-flight query's session
  survives. The schema browser is still disposed — no query path uses it.
  **Superseded 2026-08-09** (see *Amendment* below): item 1 landed as originally written after all.
- **Item 3/4** grew a piece the plan didn't call out: **status-bar routing**. Once tabs run concurrently, a
  background run's status text overwrites the status of the tab on screen, so all run status goes through
  `RunStatus`/`RunFinished` and only reaches the bar while its own tab is selected. A cancel never toasts.

Foundation (session leasing + 30-min idle eviction) already landed in `ConnectionSessionManager` (Stage A)
and is what this builds on.

## Amendment — non-destructive project switching (2026-08-09)

Stage E kept the assumption that a project switch *closes* the outgoing project: tabs cleared, sessions
closed, schema browser disposed. QA showed why that assumption doesn't hold. A long query started before
a switch survived, but its tab did not, so the completion toast reported *"the tab was closed, so the
results were discarded"* — and returning to the project rebuilt every tab from `session.json`, throwing
away result grids, FK history and pending inline edits, on a disconnected project.

A project switch is now a **view** change:

- Every project opened stays open. `WorkspaceContext` keeps a registry of `ProjectWorkspace` entries;
  switching **parks** the outgoing project's tabs on its entry and unparks the incoming project's.
  `WorkspaceContext.Tabs` remains "the active project's tabs" (what the strip, tab navigation and session
  save use); `AllTabs` is the union across projects, for anything that must not lose sight of a parked
  tab — autosave, `QuitGuard`, and `RunFinished`'s "is the tab still open" test.
- `OpenProjectAsync`/`NewProjectAsync` no longer call `CloseAllAsync` or `SchemaBrowser.DisposeAsync`
  (item 1 as originally planned). Both pools are app-lifetime; the idle sweep and credential-expiry
  eviction are the only things that reclaim a connection, exactly as if the project were still on screen.
- A tab carries `ProjectDirectory` for life. Its scratch file, session entry and connection lookup resolve
  through *that* project, not whichever is active — otherwise a parked tab's autosave would write into the
  foreground project's scratch folder.
- `SaveWorkspace` writes `session.json` for **every** open project, not just the visible one.
- Completion toasts no longer auto-dismiss (`TimeSpan.Zero`), and clicking one calls
  `ShellViewModel.RevealTabAsync` — switching project if the tab is parked in another, then selecting it.

Covered by `tests/Bearing.App.Tests/ProjectSwitchTests.cs`. Hidden projects deliberately get **no** UI
indicator (user's call): other projects' tabs are invisible until you switch back, and the toast is the
only cross-project affordance.

## Goal (user-confirmed scope)

- Multiple tabs run queries **concurrently** — starting a query in one tab must not block another.
- A running query **survives navigation**: switching tabs *and switching projects* does not cancel it.
- **Notify on completion** of a query whose tab isn't currently in view (toast). No dedicated
  "background jobs" panel/session — results just land back on the originating tab if it still exists;
  if the tab/project is gone, a completion toast is enough.
- **Per-tab cancel** (Esc + a visible stop affordance).
- **Quit** confirms-then-cancels any still-running queries (tab close does the same for that tab).

## Behavior delta

Before: one global `IsBusy` gate → a single query app-wide; switching project disposes all sessions
(`OpenProjectAsync`/`NewProjectAsync` call `_sessions.DisposeAsync()`), which would kill an in-flight query.

After: execution state is **per tab**; the session manager is **app-lifetime** (survives project switch);
a finished query routes its results to its tab if still open, else raises a completion notification.

## Work items

1. **App-lifetime sessions.** Stop disposing `_sessions`/`_schemaBrowser` in `OpenProjectAsync` and
   `NewProjectAsync`. They are disposed only on app shutdown (`DisposeSessionsAsync`, already wired to
   the exit paths). A running query's `SessionLease` already prevents idle-sweep/evict teardown; a
   project switch simply stops referencing the old project's connections (different GUIDs), and the
   idle sweep reclaims them 30 min later.

2. **Per-tab run state.** Move to `EditorTabViewModel`: `bool IsRunning`, `CancellationTokenSource? RunCts`,
   and a denormalized `ConnectionLabel` (for the completion toast, since the tab may outlive the project's
   connection list). `MainWindowViewModel.IsBusy` becomes a computed "selected tab is running" for the
   Run button; execution no longer consults a global gate.

3. **Concurrent `ExecuteAsync`.** Capture `tab` + `info` + `sql` up front; run under the tab's own CTS and
   `SessionLease`; do **not** early-return on another tab being busy. On completion:
   - if `Tabs.Contains(tab)` → `tab.SetFreshResults(...)` + status as today;
   - else → `Notify($"{label} · {summary}")` (toast) and drop the result (no jobs store).
   Apply the same per-tab treatment to `LoadMoreAsync`/`CountTotalAsync`/`SaveChangesAsync`/`NavigateForeignKeyAsync`
   (they currently gate on the global `IsBusy` and use the selected tab).

4. **Completion notification.** Add a VM event (e.g. `event Action<string>? BackgroundCompleted`) raised
   when a finished query's tab is no longer in view; the view shows it via Avalonia
   `WindowNotificationManager` (a transient toast). Needs live QA (headless can't verify).

5. **Per-tab cancel (#5).** Esc / Run-button / a per-result stop button cancel the **selected tab's**
   `RunCts` (today `CancelExecution` cancels the single global CTS — already wired to Esc at
   `MainWindow.axaml.cs:803` and the Run button at :700; repoint at the selected tab's CTS).

6. **Quit confirm.** In `App.SaveSession` (UI-thread close path) / window `Closing`, if any tab `IsRunning`,
   confirm "N queries still running — cancel and quit?" before cancelling all and closing.

## Testable core vs live-QA

- Testable (VM integration, against pagila): concurrency (two tabs run at once), survival across a
  simulated project switch, results-routed-to-tab vs completion-event-when-orphaned, per-tab cancel.
- Live QA only: the toast UI and the quit-confirm dialog.

## Risk / watch points

- Many `IsBusy` consumers (`MainWindow.axaml.cs:700,803,880`, `RunButtonText`) — audit each when `IsBusy`
  becomes "selected tab running".
- Result view models mutate `ObservableCollection` — a background completion routing to a live tab must
  marshal to the UI thread (it already runs on the UI-thread continuation today; keep that).
- Do not remove the `SafeDisposeAsync`-on-shutdown force path — a genuinely stuck query shouldn't wedge quit.
