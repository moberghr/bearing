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
>
> **2026-08-09:** settings window landed (see below). Same 3 warnings; tests now **Sql 159 · App 267 ·
> Persistence 14** green, Data 14 still skipped.
>
> **2026-08-10:** the *(live QA)* backlog is **cleared — the user QA'd everything** through Stage E, so those
> tags below are historical, not outstanding. Both open **P1**s closed this pass (honest `CountAsync`, and
> no-keyring passwords are no longer written to disk).
>
> **2026-08-10 (later): the 🔴 correctness & security section is empty.** Both remaining **P2**s and the whole
> **P3** tail were fixed in one pass, along with the `ConfigureAwait`/UI-thread item in 🟡. Three entries came
> out **narrower or different from how they were written** — read those before trusting the old wording: the
> `ex.Message` leak (credentials redacted, endpoint kept on purpose), the `secret-tool` delete (its exit code
> is *unusable*, so the postcondition is verified instead), and `GestureParser` (`(Key)16` is a real member, so
> `IsDefined` alone fixed nothing). Tests **Sql 160 · App 312 · Data 27 · Persistence 25**, run live against
> Postgres on 5434 (`BEARING_TEST_PG_PORT=5434 dotnet test` — 0 skipped); build clean with the **same 3
> warnings** (verified with `--no-incremental`; an incremental build re-emits none, which is easy to misread as
> "fixed"). New work still needs its own eyeball pass (§4.3 — Wayland).

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

## ✅ Stage E — background execution (done 2026-08-09)

Design: `docs/background-execution-plan.md`. Scope (confirmed): concurrent per-tab queries that survive
project switches + a completion toast (**no** background-jobs panel), per-tab cancel, quit-confirm.
**All five items are in.** *(live QA — the toast and both prompts are Wayland-blocked, §4.3.)*

- [x] **P2** Per-tab run state + concurrent `ExecuteAsync` — done. Each tab owns its `_runCts` +
  `IsRunning` + run clock (`EditorTabViewModel.cs:99-135`), and `ExecutionViewModel.IsBusy` /
  `RunButtonText` are an explicit **façade over the selected tab**, re-raised on selection change and on the
  watched tab's `IsRunning`. Background tabs run concurrently and independently; the global gate is gone.
- [x] **P2** Per-tab cancel (Esc / stop button) — done, via the same per-tab CTS
  (`EditorTabViewModel.cs:135`); Esc and the Run/Cancel button act on the selected tab
  (`MainWindow.Commands.cs:41`, `MainWindow.Chrome.cs:95`).
- [x] **P2** App-lifetime sessions — done. Landed first as *lease-aware `CloseAllAsync`* (teardown kept, but
  a session a running query holds leaves the live map at once and is disposed at that query's last lease
  release), then **superseded by non-destructive project switching** (below): `OpenProjectAsync` /
  `NewProjectAsync` no longer call `CloseAllAsync` or `SchemaBrowser.DisposeAsync` at all. Both pools are
  app-lifetime; the idle sweep and credential-expiry eviction are the only things that reclaim a connection.
  `CloseAllAsync` stays lease-aware and is now shutdown-only. `DisposeAsync` (shutdown) still ignores leases,
  so a stuck query can't wedge quit.
- [x] **P2** Non-destructive project switching — **done 2026-08-09** *(live QA)*. QA found the switch was a
  teardown: tabs cleared, sessions closed, so a query that outlived the switch reported *"the tab was closed,
  so the results were discarded"*, and returning rebuilt every tab from `session.json` — losing result grids,
  FK history and pending inline edits, on a disconnected project. A switch is now a **view** change. Every
  project opened stays open: `WorkspaceContext` keeps a `ProjectWorkspace` registry and **parks** the outgoing
  project's tabs (same view-model instances) rather than closing them. `Tabs` still means "the active
  project's tabs" (strip, tab navigation, session save); new `AllTabs` is the union, used by autosave,
  `QuitGuard` and `RunFinished`'s still-open test. A tab carries `ProjectDirectory` for life, so its scratch
  file, session entry and connection lookup resolve through *its own* project — `FindConnection` searches the
  registry, not just the foreground manifest. `SaveWorkspace` writes `session.json` for every open project.
  Hidden projects get **no** UI indicator by choice: their tabs are invisible until you switch back, and the
  toast is the only cross-project affordance. 9 tests in `ProjectSwitchTests.cs`.
- [x] **P2** Completion toast — done, and with it the **status-bar routing that made it necessary**. A run's
  progress and terminal text now go through `RunStatus` / `RunFinished` (`ExecutionViewModel`), which write to
  the status bar only while the run's own tab is selected — before this, a background run's "Running…" and
  summary overwrote the status of the tab actually on screen. When the tab isn't selected the terminal message
  is raised as `BackgroundCompleted(TabName, Message, TabStillOpen, Tab)` and shown by
  `Controls/CompletionToastHost` (Avalonia `WindowNotificationManager`, bottom-right, 5 max). The toast
  **never auto-dismisses** (`TimeSpan.Zero`) — a background result is the whole point of the notification, and
  timing out means missing it — and **clicking it goes to the query**: `ShellViewModel.RevealTabAsync`
  switches project if the tab is parked in another one, selects it, and the window activates. Only a genuinely
  closed tab reads `TabStillOpen: false` (a project switch parks, it doesn't close); that toast says the
  results were discarded and isn't clickable, since there is nowhere to go.
  **A cancel never toasts** — cancellation only ever comes from the user, so it stays a status line.
- [x] **P2** Quit / tab-close confirm-then-cancel — done. `MainWindow.OnClosing` blocks the close while any
  tab is running and asks (`Views/QuitGuard` + `Views/ConfirmCancelRunningDialog`); the block path
  **deliberately does not call `base`**, because that is what raises `Closing`, whose handlers save the session
  and dispose every live connection — running them for a close that isn't happening would kill the queries the
  user just chose to keep. Tab close asks the same question in `WorkspaceViewModel.CloseTabAsync`, before the
  unsaved-work prompt and **not** gated on `ConfirmTabClose` (that setting is about discarding text you can see;
  a running query's cost isn't visible the same way).
- 8 tests in `BackgroundExecutionTests.cs` (status routing, orphaned-tab completion, lease-aware
  `CloseAllAsync`, both prompts and a declined one), on top of `ConcurrentExecutionTests`.

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
- [x] **P2** **Settings screen + framework** — **done 2026-08-09** *(live QA)*. Edit ▸ Settings…, the rail
  gear, and the `settings.open` palette command all open `Views/SettingsWindow` (code-built, like
  `KeybindingsWindow`): category nav, search box, and one row per setting with an inline Reset.
  - **The window is generic — it contains no per-setting code.** It renders from
    `Core/Workspace/SettingsCatalog.cs`, a list of `SettingDescriptor`s (`BoolSetting` / `IntSetting` /
    `EnumSetting`) carrying title, description, keywords, range/options and **typed** get/set lambdas over
    `AppSettings`. No reflection and no runtime key lookup, so a renamed property breaks the build rather
    than the window. **Adding a setting is two edits in one folder** — a property on `AppSettings` and a
    descriptor in the catalog — and it then renders, searches, persists and resets for free. A new *kind*
    of value costs one subclass plus one arm in `BuildControl`.
  - **`SettingsCatalogTests` enforces that contract**, most usefully
    `Every_setting_is_either_described_or_declared_hidden`: an `AppSettings` property with neither a
    descriptor nor an entry in the test's `HiddenState` list fails the build's test run. That's the guard
    against the catalog quietly drifting behind the model.
  - **Edits apply immediately — there is no Save/Cancel** (deliberately unlike `KeybindingsWindow`, which
    edits a keymap as one unit): a control change goes through the new `App/Settings/SettingsService`,
    which applies, persists and broadcasts `Changed`. A no-op edit neither writes nor broadcasts. Rows
    that *can't* take effect at once (query-log retention, page size) carry an `AppliesNote` saying so,
    and a test asserts those two have one.
  - **An unwritable settings file reports and keeps the edit** rather than reverting under the cursor
    (§5.2) — `SaveFailed` goes to the status bar, nothing throws.
  - `WorkspaceContext.Settings` is now a *property* over the service (`=> SettingsService.Current`), not a
    snapshot, so every existing `_ctx.Settings.X` read became live with no subscription. Only consumers
    that genuinely cache subscribe: the idle sweep (see below) and the shell's `EditorFontSize` mirror.
  - **Six settings ship, four of them newly wired**: autosave mode and query-log retention (previously
    file-edit only), plus editor font size (was a hard-coded `FontSize="14"`), connection idle timeout
    (`ConnectionSessionManager.IdleTimeout` is now settable and reschedules its sweep), result page size
    (`ExecutionViewModel.PageSize` const → read-per-use property), and confirm-on-tab-close.
  - `SettingsSearch` (pure, in `App/Settings/`) does token-substring matching over title/description/
    keywords/key with a fuzzy-subsequence fallback on the title, reusing `PaletteFilter.Score`. Sections
    keep declaration order; ranking applies within a section; empty sections drop out.
  - Store side: `IAppSettingsStore` added to `Core` (`AppSettingsStore` already had atomic write +
    defaults-on-bad-file), plus `FakeSettingsStore` in `Fakes.cs` and `SettingsService.InMemory(...)` for
    headless construction. `ShellViewModel`'s `settings:` parameter is now a `SettingsService`.
  - 31 tests across `SettingsCatalogTests` / `SettingsServiceTests` / `SettingsSearchTests` /
    `SettingsWiringTests` — the last one proving each setting changes what the app *does* mid-session.
  - **Not done:** TLS/`sslmode` preference and completion toggles (neither exists to configure yet); a
    Completion section will appear when they do.
  - **Seventh setting added 2026-08-10** — a new **Security** section holds
    `security.allowUnencryptedSecretFile` (see the fallback-secret-store item). It cost exactly what the
    framework promised: one `AppSettings` property plus one catalog descriptor, no window changes.

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
  - **Now has a UI** (2026-08-09): Settings ▸ Editor ▸ Autosave, as three named choices. The mode was
    file-edit-only when this shipped.
- [x] **P3** **A failed schema expand is sticky until Refresh** — **closed 2026-08-11 as won't-fix (user's
  call).** The behaviour is unchanged and intentional: `EnsureChildrenAsync` still sets `_loaded = true`
  *before* the load (`SchemaNodes.cs:75`), so collapse-and-re-expand replays the stale error rather than
  retrying. **The retry that matters already exists and is one gesture away** — right-click the server ▸
  *Refresh metadata* (`SidebarView.axaml:43` → `ConnectionsViewModel.RefreshServerMetadataAsync:325` →
  `SchemaNodeViewModel.RefreshAsync:95`), which resets `_loaded = false` *and* drops the schema-browser's
  per-database readers, so the retry runs on a fresh connection instead of the one that failed. Making
  re-expand retry on its own would only duplicate that, and silently re-hit a dead connection on every
  collapse. (Surfaced 2026-08-06 while chasing an Entra connection error that turned out not to reproduce.)
- [ ] **P2** Remove / delete projects. Delete a project from the recent list, and optionally from disk (with confirm). Also prune stale/missing entries from the recent list (see the P3 recent-projects item below).
- [x] **P3** Restore last window size on startup — **done 2026-08-09** *(live QA)*, with the settings work.
  `AppSettings.WindowWidth`/`WindowHeight` are persisted state (no catalog row) and the visible toggle is
  `general.restoreWindowSize`. Written from `App.axaml.cs` on `Closing` and only when the window is in the
  `Normal` state, so un-maximizing doesn't come back at the maximized size. **Position is deliberately not
  persisted** — left to the window manager, which is also the only thing that works under Wayland.
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
- [x] **P2** Show the SQL in the write-confirm dialog — **done 2026-08-11** *(live QA)*, together with the
  Preview-SQL rework below (they were one job). `ConfirmWriteAsync` no longer takes a connection + verb list
  but a `Services/WriteConfirmation` — target connection, `WriteAction` (batch vs. inline save), verbs, and
  **the statements about to run**, plus all the derived display text (heading / summary / warning / button
  label / copyable script), which is where the coverage lives (`WriteConfirmationTests`) since the dialog
  itself can't be driven headlessly. New `WriteGuard.Describe(sql)` → `StatementRisk` per statement (text,
  leading verb, risky verbs found in it) is the same lexer scan `FindRiskyStatements` does, no longer
  projected down to distinct verbs — that one is now a two-line projection over `Describe`, so the guard
  can't drift from what the dialog shows. Reads in a mixed batch are listed too (dimmed, no tag colour):
  the batch runs them, and "1 of the 12 statements below will modify data or schema" is the honest summary.
  One behaviour change on the guarded path: **Enter no longer commits** when the connection requires write
  confirmation (Cancel takes `IsDefault`, proceeding takes a click) — Enter-through defeats the point of the
  guard (§1.2). An ordinary inline save keeps Enter on the confirm button.
- [x] **P2** Rework the edit **Preview SQL** flow — **done 2026-08-11** *(live QA)*. **Inline saves now
  always confirm** (user's call, 2026-08-11), so the DML preview sits on the path to committing instead of
  being a step you had to remember to take: `SaveChangesAsync` builds the statements, confirms, *then* takes
  the session lease (a modal prompt must not hold a session open while the user reads it — it did before).
  A guarded connection gets an extra amber warning line, not the only prompt. Retired: the `‹ › Script`
  button, `ResultView.PreviewSql`, `MainWindow.ShowPendingScript` / `MainWindow.Overlays.cs`,
  `Controls/PendingChangesOverlay.cs`, and `ExecutionViewModel.PreviewChanges` (already dead). The panel's
  line-numbered, kind-coloured rendering survives as `Controls/SqlStatementList` inside the dialog, capped at
  100 rendered statements (with a "… and N more" line — Copy always carries the whole script).
  `PreviewChangeStatements` → `PendingWriteStatements`, now returning `WriteStatement`s. Removing the
  overlay also removed its five modal-key hacks from `MainWindow.Commands.cs` (Escape unwinding, the
  swallow-globals-except-Escape branch, the nav-key block, the hide-on-rebuild call) — a real modal window
  needs none of them.
- [ ] **P2** **Export results to Excel** (+ CSV) with an "open containing folder" action after the export
  completes. Decide the xlsx route (a package vs. hand-rolled OOXML — note §0.1: nothing new in `Core`);
  export should offer loaded-rows vs. whole-result (needs the fetch-all item below). **The button already
  exists and is deliberately dead** — `Controls/ResultEditToolbar.cs` renders `⭳ Export` with the tooltip
  "Export — coming soon" ("rendered; wired later (per decision)"), so this is wiring a present affordance,
  not adding one. Nothing else export-related exists in `src/`.
- [ ] **P2** **Fetch all rows** button on a paged result — one action that loops `LoadMore` to completion
  (cancelable, with progress/row count) instead of clicking through pages. Guard the obvious foot-gun on huge
  results. `ResultSetViewModel.IsPageable/HasMore/AppendPage`, `ExecutionViewModel.LoadMoreAsync` (`PageSize` 100).
- [ ] **P2** **Copy as…** — extend grid copy beyond TSV: HTML, Markdown, JSON, CSV, and SQL (`INSERT`
  statements / `VALUES` list). Context menu + palette commands; pure formatters under `Results/`
  (§2.5, testable without a grid) reusing `GridSelectionOps.Rectangle` / `Tsv`.
- [ ] **P2** **Paste into the results grid (`Ctrl+V`)** — copy is done, paste doesn't exist. Requested
  2026-08-11: copy the selected cells' values, then select one or more cells and paste, editing those rows.
  Copy already works (`grid.copy`, `Ctrl+C`/`Ctrl+Insert` → `GridSelectionOps.Tsv`); there is **no**
  `grid.paste` in `CommandIds` and nothing in `src/` reads the clipboard, so this is all new.
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
- [ ] **P2** **ISO dates in the grid and on the clipboard** — requested 2026-08-11. Copy and display share one
  formatter (`GridSelectionOps.CellText` → `CellFormat.Display`), so **changing the three patterns in
  `Formatting/CellFormat.cs` fixes both at once** — that's the "make it ISO when displaying as well"
  simplification, not two changes. Today: `DateTimePattern` `dd.MM.yyyy HH:mm:ss`, `DatePattern`
  `dd.MM.yyyy`, `TimePattern` `HH:mm:ss` (already `InvariantCulture`).
  - **Decide the exact form**: `yyyy-MM-dd HH:mm:ss` (space, far more readable in a grid, still RFC 3339)
    vs. strict `yyyy-MM-ddTHH:mm:ss`. Also decide fractional seconds — the current `TryParseExact` on
    `dd.MM.yyyy HH:mm:ss` **fails** on any value carrying them and falls through to the lenient parse.
  - **`DateTimeOffset` must keep its offset** (`…K`, or round-trip `o`). The current pattern drops it
    entirely, so copying a `timestamptz` today loses the zone — an existing data-loss-on-copy bug that this
    change is the natural moment to fix.
  - **Round-trip gets strictly better**, which is the real argument: `CellFormat.TryParseDate` tries the
    display pattern first, so an ISO display means an edited or pasted date re-parses unambiguously instead
    of depending on which way the current culture reads `03.04.2026`.
  - Rewrite `CellFormat`'s class doc comment — it currently *justifies* the day-first pattern ("`.NET`'s
    culture data for day-first locales adds spaces/trailing dots"), so leaving it would contradict the code.
    Update `CellFormatTests` (patterns are asserted there) and note the visible change for eyeball QA.
- [ ] **P2** Checkbox (bool) cells don't take grid selection — clicking one toggles the value but leaves the
  cell/row selection where it was, so keyboard nav and copy act on the wrong cell. Make the bool cell set the
  selection on click like every other cell. `Controls/ResultCellFactory.cs` `BoolCell`.
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
- [x] **P1** `CountAsync` swallowed all errors as "uncountable" → paging hid totals on a real DB failure.
  **Fixed 2026-08-10.** Null now means only that the query's *shape* can't be wrapped in
  `select count(*) from (…)` — matched on SQLSTATE `42601` (multi-statement / non-SELECT) and `0A000`
  (data-modifying CTE, which must be top-level). Every other failure propagates, so the VM's existing
  handler reports "Count failed: …" while `TotalCount` stays null and `CanCount` stays true, leaving
  `[Count]` available to retry. The null-vs-throw contract is stated on `IDbProvider.CountAsync`.
  +1 live `SkippableFact` (`PostgresExecutorTests`, verified against a real server: multi-statement and
  `update` → null, undefined table → `42P01` thrown, cancelled token → thrown) and +3 headless status-bar
  tests (`CountTotalTests`, new `PageableExecutor` fake).
- [x] **P1** `StatementSplitter`: trailing `-- line comment` swallows the auto-appended `;` (merges statements → syntax error); blank-line heuristic mis-splits a single statement with a blank line at paren depth 0. `StatementSplitter.cs`. **Fixed:** `EnsureSeparated` puts the `;` on its own line after a fragment ending in a line comment; the blank-line split now fires only when the next token starts a statement (`StartsStatement`) and the previous token isn't a set operator (`EndsWithSetOperator`), so a statement continued by `and`/`order by`/`union` no longer mis-splits. (+4 tests)
- [x] **P1** `CellFormat.FormatArray` throws on multi-dimensional Postgres arrays (uses `arr.Length` with single-index `GetValue`). `Formatting/CellFormat.cs:36-41`. **Fixed:** `foreach` flattens any rank in row-major order instead of single-index `GetValue`. (+1 test)
- [x] **P2** `EnsureSchemaAsync` inflight keyed by ConnectionId not (id, database) → wrong-DB snapshot across a
  rebuild. **Fixed 2026-08-10:** `_schemaInflight` is now keyed by `(Id, Database)`, so a DB switch that
  replaces the session starts its own load instead of joining the outgoing database's and adopting its
  snapshot (which decides editability and FK targets). Session keying is untouched, per §9.4. The removal in
  `LoadSchemaAsync`'s `finally` uses the same key, so a stale load can't evict its replacement's entry.
  +2 tests (`ConnectionSessionManagerTests`) — one pinning the split, one pinning that two callers on the
  *same* session still share a single read; the metadata fake gained a gate to hold a load in flight.
- [x] **P2** Missing `ConfigureAwait(false)` in the data layer — **done 2026-08-10, and the original framing
  was wrong.** The entry claimed *deadlock risk for any sync-over-async caller*; there is no such caller. The
  only blocking wait in `src/` is `TabAutosave.cs:147`, already wrapped in `Task.Run(...)` (safe — no context
  inside), and `ISessionStore.Save` exists as a sync API precisely so shutdown never blocks on async. The
  deadlock argument was future-proofing, not a live bug.
  **The real cost was UI-thread scheduling.** Avalonia installs a `SynchronizationContext` on the UI thread
  (unlike ASP.NET Core, which is where "you don't need `ConfigureAwait` anymore" comes from — .NET 10 changes
  nothing here), and there is no `Task.Run` anywhere in the query path
  (`MainWindow.Commands.RunAsync` → `ExecutionViewModel.ExecuteAsync` → executor). Since `ReadResultSetAsync`
  awaits **once per row**, every await that actually suspended — buffer empty, next socket read — posted its
  continuation to the dispatcher, so result materialization ran on the UI thread in bursts, competing with
  rendering. Harmless at 100 rows/page; not harmless once **Fetch all rows** exists.
  Applied to every suspending await in `Bearing.Data` + `Bearing.Persistence` (~85 call sites). Plain
  `await using` declarations over *synchronously* constructed resources (`new NpgsqlCommand`, `File.Create`)
  are deliberately left alone: a synchronously-completed await posts nothing, and their disposals run after an
  earlier configured await has already moved off the UI context. The convention + that reasoning is documented
  on `PostgresQueryExecutor`.
  Also in the same loop: `await reader.IsDBNullAsync(i, ct)` per **cell** → sync `reader.IsDBNull(i)`. The
  reader isn't in sequential-access mode, so `ReadAsync` has already buffered the row and the null check can't
  touch the socket — the async form bought an awaited state-machine hop per cell to await an
  always-completed task. +1 live test pinning that null cells still materialize as null without shifting
  their neighbours (`Null_cells_materialize_as_null_without_disturbing_their_neighbours`).
- [x] **P2** `ForeignKeyResolver` assumed equal-length parent/referenced attnum lists → `IndexOutOfRange` on a
  malformed composite FK. **Fixed 2026-08-10:** the counts are compared up front and a mismatched constraint
  is skipped — a composite FK pairs its columns one-to-one, so unequal lists mean the pairing is meaningless,
  not that it should be attempted halfway. +1 test in `ForeignKeyResolverTests`.
- [x] **P2** Write-guard gap: inline result-grid saves bypass the confirm dialog. **Already fixed** (landed in the merged review-fixes) — `ExecutionViewModel.SaveChangesAsync:245-254` confirms via `ConfirmWriteAsync` when the connection has `RequireWriteConfirmation`, mirroring the `ExecuteAsync` gate.
- [x] **P3** `NpgsqlConnectionFactory` applied persisted options verbatim. **Fixed 2026-08-10:** the option bag
  is now filtered in three ways — a `Reserved` set (password/host/port/database/user + aliases) can never be
  overridden from `Options`, since identity and credentials come from `ConnectionInfo` + the secret store; keys
  the driver doesn't own are ignored rather than thrown at connect, which is what made the documented
  `entra.resource` override unusable; and a genuine Npgsql keyword is still applied, with a bad *value* still
  throwing (a typo worth surfacing). This unblocks per-connection `entra.*` options. +4 tests
  (`ConnectionOptionsTests`, three live: app-level key connects fine, an `Options["Password"]` can't displace
  the real one, and `ApplicationName` set through `Options` is observable in `pg_stat_activity`).
- [~] **P3** Raw `ex.Message` surfaced to the UI on generic catch paths. **Partly done 2026-08-10, and
  deliberately narrower than written.** New pure `Core/Data/SafeErrorText` redacts `password=…` /
  `pwd=…` values out of driver messages — the real hazard, since a connect- or parse-time failure can quote the
  whole connection string, which then lands in the results pane, the status bar *and* the query log. Wired into
  the executor's three generic catches and the connect-failure path (`ConnectionSessionManager`).
  **Host/port/database are kept on purpose:** this is a local tool showing the user a server they configured
  themselves, the connect path already names the endpoint by design (`Could not connect to 'x' (host:port/db)`),
  and stripping it would remove the useful half of every DNS/TLS/network error while protecting nobody. If the
  endpoint should genuinely be hidden, that's a separate decision — say so and it's a one-line change.
  +5 tests (`SafeErrorTextTests`).
- [x] **P3** `SchemaBrowser.BuildAsync` catch removed the cache key unconditionally → could evict a concurrent
  replacement (pool leak). **Fixed 2026-08-10:** eviction moved out of `BuildAsync` into a
  `NotOnRanToCompletion` continuation registered by `GetReaderAsync`, which removes the entry only while it is
  still `ReferenceEquals` to *this* attempt's task. Retry-after-failure behaviour is unchanged (and now also
  covers a cancelled build, which previously stayed cached). **Not directly tested** — reproducing it needs a
  failing build racing a replacement through the credential path; the invariant is stated at the call site.
- [x] **P3** `JsonSessionStore` — atomicity was already fixed; **`LoadAsync` resilience done 2026-08-10.** A
  truncated, empty or hand-edited `session.json` now loads as "no session" (defaults + one empty tab) instead
  of throwing out of project open — a disposable cache of window layout could stop a project from opening.
  Matches `AppSettingsStore`; `OperationCanceledException` still propagates. +5 tests
  (`StoreResilienceTests`, including a valid-file round-trip so the catch can't mask a real regression).
- [x] **P3** `secret-tool` delete ignored its exit code → a failed clear left a stale credential after
  "delete". **Fixed 2026-08-10, but not by checking the exit code:** measured on this machine, `secret-tool
  clear` exits **1 both for a real failure and for "nothing matched"**, with an empty stderr in the latter case
  — so trusting the code would make every delete of a password-less connection report an error. `DeleteAsync`
  now verifies the *postcondition* on a non-zero exit: it looks the secret up, and throws only if it's still
  there. +1 `SkippableFact` (`StoreResilienceTests`) that stores, deletes, verifies it's gone, and then deletes
  twice more to prove "nothing to clear" is not an error; it ran live against the local keyring.
- [x] **P3** `ResultSetViewModel.ToggleDelete` un-delete dropped a prior pending edit. **Fixed 2026-08-10:** the
  superseded edit is parked in a new `_editedUnderDelete` set and restored when the row is un-marked, so the
  values the grid is still displaying will actually be saved. `RevertPending` also rolls those cells back now
  (it iterated `_edited` only, so a parked edit survived a revert on screen), and `ClearPending` clears the new
  set. Delete still supersedes the edit at save time — that part was deliberate. +2 tests (`PendingEditTests`).
- [x] **P3** `ChangedAssignments` compared the edited *string* against the typed original → no-op UPDATE
  assignments. **Fixed 2026-08-10:** the value is coerced first and compared after, so a cell typed back to
  what it already held generates no assignment (it used to re-write every touched column — and re-fire audit
  triggers on them). The existing `assignments.Count > 0` guard means such a row now produces no UPDATE at all
  rather than invalid SQL. +2 tests (`PendingEditTests`).
- [x] **P3** `GestureParser` accepted numeric/undefined enum values. **Fixed 2026-08-10** via one
  `TryParseKeyName<T>` helper used by both the logical and physical paths: the token must be letter-leading
  (rejecting `16`, `0x10`, `-1`) *and* `Enum.IsDefined`. Worth knowing why `IsDefined` alone wasn't enough —
  `(Key)16` is a real member (`ImeAccept`), so `Ctrl+16` silently bound a valid-but-unrelated IME key rather
  than an invalid one. +10 theory cases (`InputRobustnessTests`), including four that must still parse.
- [x] **P3** MRU tab-cycle state hard-coupled to the Ctrl key — rebinding `tab.mruNext` froze MRU ordering.
  **Fixed 2026-08-10:** new pure `Input/MruCycle` (§2.5) derives the held modifier from the *keymap*'s bindings
  for `tab.mruNext`/`tab.mruPrev` and answers whether a released key ends the cycle; `MainWindow` just asks.
  Two subtleties are pinned by tests: Shift is excluded (it only picks direction, so releasing it mid-cycle
  must not commit early), and a modifier-less binding reports `None`, where `CycleMru` commits immediately
  because no key-up is ever coming. +4 tests (`InputRobustnessTests`).
- [x] **P3** `StreamAsync` ignores `QueryOptions.MaxRows` — **closed by deleting the dead API.** No `StreamAsync` remains anywhere in the repo. (`QueryOptions.MaxRows` itself is live and honoured on the paging path: `PostgresQueryExecutor.cs:145`, set to `PageSize` by `ExecutionViewModel.cs:192,326`.)
- [x] **P3** Recent-projects dropdown wasn't pruned of missing dirs. **Fixed 2026-08-10:** `RefreshRecentAsync`
  skips an entry whose directory is gone *and* drops it from the store (new `IRecentProjects.RemoveAsync`, so
  it self-heals instead of re-checking forever). Pruning is on directory existence only, deliberately: a folder
  that exists but has no manifest yet keeps its entry, so an unmounted path or a mid-rewrite manifest isn't
  silently forgotten. +2 App tests (`RecentProjectsPruneTests`) + 1 Persistence test for `RemoveAsync`.

---

## 🟡 Open — quality & maintainability

- [x] **P2** Decompose the god objects — **done 2026-08-10.** All three are gone. Build clean, 562 tests green
  (App 312 → 369), app launches clean 3/3 runs. **Both views still need eyeball QA** — Wayland blocks headless
  GUI testing (§4.3), so nothing visual or interactive here is verified.
  - **`MainWindowViewModel` (1014) — done earlier.** `ShellViewModel` (153 + `.Session` 59 + `.Projects` 158)
    delegates to child VMs behind `WorkspaceContext` — `WorkspaceViewModel` 153, `ConnectionsViewModel` 423,
    `ExecutionViewModel` 422, `ScriptsViewModel` 149, `HistoryPanelViewModel` 130. This was the pattern the
    other two followed.
  - [x] **`Controls/ResultView`** — **1,902 → 497** over 3 partials (root 175, `.Layout` 192, `.Grid` 130).
    It is now the composition root for the dock and nothing else. Extracted: `GridSelectionController` 275
    (+ pure `Results/GridSelectionOps` 146), `ResultCellFactory` 305, `ResultChrome` 293 (badges, buttons,
    drawn glyphs, lock chip, back bar, dock header), `CellInspectorView` 219 + `InspectorPane` 95,
    `ResultEditToolbar` 107, `ResultGridChrome` 102, `QuickStatsBar` 99, `ResultRowPainter` 58, and pure
    `Results/{ResultMetaText 46, ColumnKinds 40}`.
  - [x] **`Views/MainWindow`** — **1,167 → 823** over 5 partials (`.Commands` 334, `.axaml.cs` 289,
    `.Chrome` 128, `.ConnectionCommands` 46, `.Overlays` 26). `.Commands` is now the command table plus key
    routing; `.Chrome` is the XAML-wired handlers. Extracted: `Editing/EditorTextCommands` 143 (statement ops,
    the Ctrl+U/W deletes, the statement-highlight margin, and "what SQL does Run execute"),
    `Views/CommandPaletteHost` 136, `Input/TabNavigator` 91, `Views/ResultsPaneController` 65,
    `Input/FocusRing` 60, `Editing/EditorChrome` 42.
  - **Bonus:** six private copies of the token-brush lookup (`ResultView.Res`, `MainWindow.ThemeBrush`,
    `SettingsWindow.Brush`, `KeybindingsWindow.Brush`, `FilterableListOverlay`, `PendingChangesOverlay`)
    collapsed into `Controls/Tokens` (`Res` / `Tint`). `Theming.ThemeBrush.AtAlpha` deliberately stays — it
    takes an explicit fallback colour for converter/margin contexts.
  - **New tests (57)** over the logic that was previously unreachable inside the two classes:
    `GridSelectionOpsTests` (cursor motion, rectangle coverage, TSV shape incl. non-rectangular gaps,
    measure-column filtering), `ResultMetaTextTests`, `ColumnKindsTests`, `ShellNavigationTests`
    (tab wrap/clamp, focus-ring order).
- [x] **P3** Remove dead `Views/HistoryWindow.axaml(.cs)` — **done**, the files are gone; the inline History panel is the only history UI.
- [~] **P3** The "coming soon" stubs. *(`About` done — `Views/AboutDialog.cs` shows name/tagline/version from `<Version>` in `Directory.Build.props`. `Settings` done 2026-08-09 — the rail gear and Edit ▸ Settings… open the real window.)* **One** remains and it belongs to another item: **Export** (`Controls/ResultEditToolbar.cs`, a rendered-but-unwired `⭳ Export` button) → the export item above.
- [~] **P3** Clear build warnings — **3 remain**, verified this pass; the obsolete `TextBox.Watermark` → `PlaceholderText` set is **fixed**. Left: `CS0108` `StatementMargin.Width` hides `Layoutable.Width` (`Editing/StatementMargin.cs:16`), and `xUnit2013` ×2 (`HistoryPanelTests.cs:51,53` — use `Assert.Single`).

---

## 🔵 Open — hardening backlog (pre-existing)

- [~] **P1** Fallback secret storage. **The exposure is closed as of 2026-08-10, by not storing rather than by
  encrypting** (user's call over keychains / machine-key / passphrase encryption): with no OS keyring,
  `FileFallbackSecretStore` **refuses** a new password (`ISecretStore.CanStore` false, typed
  `SecretStorageRefusedException`) instead of writing base64 to `~/.local/share/bearing/secrets/`. Such
  connections keep the secret in memory for the session — a new connection defaults to *Prompt each time*,
  the dialog warns instead of silently not-saving, saving reports "password not saved (no keyring); you'll be
  asked when connecting", and a `StoredPassword` connection with nothing stored connects passwordless first
  (so trust auth / `.pgpass` still work) and is prompted by the existing one-shot auth-retry. Reads and
  deletes still work, so secrets written before the change keep resolving and can be cleared. The old
  behaviour is one opt-in away: Settings ▸ Security ▸ "Store passwords on disk when no keyring is available"
  (`AppSettings.AllowUnencryptedSecretFile`, read live — no restart), which keeps the amber
  base64 warning. +4 `SecretStorePolicyTests`, +4 `NoKeyringConnectionTests`.
  **Still open:** platform keychains (Windows DPAPI / macOS Keychain — Linux libsecret is the only real store
  wired) and any actual encryption of the opt-in file. Both were declined as untestable here / not worth the
  key-management story; the file path is now off by default, so neither is load-bearing.
- [ ] **P2** Query-log privacy: file perms (0600), optional encryption, and/or PII/literal stripping. Retention exists (default 180d); no stripping. `SqliteQueryLog.cs`.
- [ ] **P2** Enforce/prompt TLS (`sslmode`) — currently only set if the user adds the option manually.
- [x] **P3** Settings UI for query-log retention and the idle timeout — **done 2026-08-09** with the
  settings window; the idle timeout is no longer a fixed constant and applies without a restart.
- [ ] **P3** CI pipeline (build + `dotnet test`). Previously skipped.
- [x] **P3** Deeper VM decomposition — **done for the VM half**: the stateful coordinators
  (connections / execution / tabs / panels) now live in `ConnectionsViewModel`, `ExecutionViewModel`,
  `WorkspaceViewModel`, `ScriptsViewModel` behind `WorkspaceContext`. The overlay-code-behind half is
  tracked in the god-object item above, not here.

---

## Notes

- GUI can't be driven headlessly here (Wayland), so every UI change needs a manual pass. **The backlog of
  never-QA'd features is cleared (2026-08-10)** — the rename, credential sources, connection status, project
  switching and Stage E have all been eyeballed. Keep tagging new UI work *(live QA)* until it has been.
  Outstanding from this pass: the connection dialog's no-keyring warning and Settings ▸ Security only appear
  on a machine without libsecret, so they can't be seen on the dev box at all without unsetting the keyring.
- Keep this file honest by re-checking file:line references when you touch an item. The 2026-08-06
  reconciliation found the two systematic drifts to watch for: **the partial-class split** moved a lot of
  code (`MainWindowViewModel` → `ShellViewModel` + child VMs, `MainWindow`/`ResultView` into partials), so
  pre-split line references are wrong; and **five items had quietly closed** (`StreamAsync`,
  `HistoryWindow`, the `Watermark` warnings, half of `JsonSessionStore`, the VM decomposition) while their
  entries still read as open.
- Cross-cutting overlaps worth batching rather than doing twice:
  - **Window-closing hook** — **built** by Stage E (`MainWindow.OnClosing`, quit-while-running confirm).
    Anything else that needs to intervene at quit hangs off it; note the not-calling-`base` contract above.
    The scratch save-prompt turned out **not** to need it: quitting already round-trips every buffer through
    `session.json`, so that prompt is tab-close only.
  - **Write-confirm dialog** — **built** (2026-08-11). Showing the SQL and reworking Preview SQL were one
    job, done as one. Anything else that wants to confirm a write builds a `Services/WriteConfirmation`;
    anything that wants to *show* SQL statements reuses `Controls/SqlStatementList`.
  - **`ConnectionInfo.Options` handling** — the P3 factory item is the prerequisite for any app-level
    (`entra.*`) option key, since unknown keys currently reach Npgsql and throw.
  - **Settings framework** — **built** (2026-08-09). Anything that wants to become configurable now costs
    a property plus a catalog entry; see the settings item above. The one option still wanted and not
    there is the TLS/`sslmode` preference, which needs the connection-level work first.
  - **Editor text ops** — the `Ctrl+U`/`Ctrl+W` helpers landed as `Bearing.Sql/TextDeleter.cs`; carving up
    `MainWindow.Commands.cs` still touches the same code, and `EditorSpan`/`ApplyDelete` are the shape the
    rest of the editor ops should be pulled into.
- The `SELECT … INTO` write-guard case and the recent-project pruning were partially informed by, and
  partially close, review findings — cross-check before re-doing.
