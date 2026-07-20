# Background execution plan (Stage E)

Status: **planned, not started.** Foundation (session leasing + 30-min idle eviction) already landed in
`ConnectionSessionManager` (Stage A) and is what this builds on.

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
