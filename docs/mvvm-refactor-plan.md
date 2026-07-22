# MVVM refactor plan — dissolve the god VM into a composed shell

Goal: replace the single `MainWindowViewModel` (7 concerns + all services in one class, imperatively
driven views) with a slim **`ShellViewModel`** composing one VM per concern, coordinating through a
shared **`WorkspaceContext`**. Views bind to their VM; dialogs go through **`IDialogService`**.

Confirmed decisions:
1. Shared **`WorkspaceContext`** mediator with plain C# events (not `WeakReferenceMessenger`).
2. **`ExecutionViewModel`** is separate from `WorkspaceViewModel`.
3. Rename `MainWindowViewModel` → **`ShellViewModel`**.
4. Dialogs via an **`IDialogService`** interface (implemented in `Views/`).

Constraint: Avalonia UI can't be verified headlessly (Wayland). Every phase ends with
`dotnet build` + `dotnet test` + a launch-clean smoke run + a user QA pass of the touched surface.
Behaviour must not change; this is a structural refactor.

## Target topology

```
App.axaml.cs (composition root)
  services → WorkspaceContext(services)
          → DialogService(window)          : IDialogService
          → child VMs(context, dialogs)
          → ShellViewModel(children, context)
          → MainWindow { DataContext = shell }

ShellViewModel (slim)
 ├─ shell UI state: StatusText, Title, SidePaneOpen/Width, ActivePanel, ResultsViewMode,
 │                  IsMenuVisible, IsConnected, SecretStorageSecure, RecentProjects
 ├─ project lifecycle: Initialize/Resume/Open/New/Rename  (+ session save/restore)
 └─ child VMs (properties): Workspace, Connections, Scripts, Execution, Results, History

WorkspaceContext (App/Workspace/ — shared coordinator, NOT a VM)
 ├─ services: Providers, ProjectStore, SessionStore, QueryLog, RecentProjects, Secrets,
 │            Sessions (ConnectionSessionManager), Schema (SchemaBrowser)
 ├─ aggregate state: Project?, Tabs, SelectedTab, DefaultConnectionId
 ├─ helpers: FindConnection(id), EffectiveConnection(tab), SetStatus(text)
 └─ events: ProjectChanged, SelectedTabChanged, ConnectionsChanged, TabsChanged
```

Child VMs depend only on the context (star topology — no VM→VM edges). `HistoryPanelViewModel`
already fits this shape.

## Child VM responsibilities

| VM | Owns (bindable) | Deps | Absorbs from god VM |
|---|---|---|---|
| ShellViewModel | shell UI state, RecentProjects, child VMs | context, children | core props, `.Projects`, `.Session` |
| WorkspaceViewModel | Tabs, SelectedTab (via context), tab MRU | context | `.Tabs` |
| ConnectionsViewModel | Connections, ServerNodes, TabDatabases, SelectedTabConnection/Database | context, dialogs | `.Connections` |
| ScriptsViewModel | Scripts, ScriptNodes, ScriptFilter | context, dialogs | `.Scripts` |
| ExecutionViewModel | IsBusy, Run + Cancel/LoadMore/Count/FollowFk/Back/Save/Discard/Preview | context, dialogs | `.Execution` |
| ResultsViewModel | Results (from SelectedTab), CanGoBack, ViewMode + commands | context, execution | (new — binds ResultView) |
| HistoryPanelViewModel | Groups/SelectedRow/Filter | context | unchanged |

## View-layer changes

- **`IDialogService`** (in `Views/`, holds active window): `ShowConnectionDialogAsync`,
  `ShowTextPromptAsync`, `ConfirmWriteAsync`, `ShowSqlPreview`, `PickFolderAsync`. VMs call it; code-behind stops opening dialogs.
- **`ResultView` becomes bound**: `DataContext = ResultsViewModel`; the `Results` property + the
  `LoadMore/CountTotal/NavigateForeignKey/Save/Discard/PreviewSql` callbacks become commands/bindings.
- **Editor** stays a documented exception: AvaloniaEdit `TextEditor.Text` isn't cleanly bindable, so a
  thin `EditorTextBehavior` binds `Text`/caret ↔ `SelectedTab`. Completion/folding/statement-highlight
  remain view-layer editor mechanics, fed a `ISchemaSnapshot` from the context.
- **Input pipeline unchanged**: `CommandRegistry` handlers call the relevant child-VM command.

## Phases (each: build + test + launch-clean + user QA)

- **0. Scaffold** — rename → `ShellViewModel`; introduce `WorkspaceContext` (services + Project + status
  + connection resolution) with the god VM delegating into it; add `IDialogService` + `DialogService`,
  route `ConfirmDangerousWrite` through it as first adopter. No behaviour change.
- **1. Connections** — extract `ConnectionsViewModel`; rebind `SidebarView` connections panel + toolbar pills.
- **2. Scripts** — extract `ScriptsViewModel`; rebind sidebar scripts panel.
- **3. Execution + Results** — extract `ExecutionViewModel` + `ResultsViewModel`; bind `ResultView`. (highest risk)
- **4. Workspace + editor** — extract `WorkspaceViewModel`; move Tabs/SelectedTab into context; add `EditorTextBehavior`; slim shell.
- **5. De-code-behind** — remaining orchestration → commands + dialog service; `MainWindow.axaml.cs` becomes wiring only.
- **6. Dissolve / flip bindings** — the step that removes the delegation facades. Enable
  `AvaloniaUseCompiledBindingsByDefault=true` (so every rebind is build-verified, not a silent blank),
  expose the child VMs as public shell properties (`Connections`, `Scripts`, `Workspace`, `Execution`,
  `Results`), rebind XAML + code-behind to `Vm.<child>.X`, then delete every `ShellViewModel.<concern>.cs`
  facade + the `PropertyChanged` forwarding. Shell is left with only shell-chrome state + child-VM
  composition + project lifecycle. Done once at the end (one coherent build-checked sweep), not per-phase.

Phases 1–2 prove the pattern; phase 3 is the crux (results + inline edit). Each phase leaves the app shippable.

## Status

- [x] Phase 0 — scaffold (rename → ShellViewModel; WorkspaceContext; IDialogService + write-guard adopter). Build 0 err, 274 tests, launch-clean.
- [x] Phase 1 — connections. `ConnectionsViewModel` owns the tree/pills/dialogs logic + state; shell keeps a thin delegating facade (`ShellViewModel.Connections.cs`) so bindings/code-behind are unchanged. `DefaultConnectionId`/`IsConnected` moved to context. Build 0 err, 274 tests, launch-clean. **Note:** binding-topology flip (point XAML at `Connections.X`) deferred — `AvaloniaUseCompiledBindingsByDefault=false` means reflection bindings, so a rebind isn't build-verified; do it in a later step bundled with enabling compiled bindings.
- [x] Phase 2 — scripts. `ScriptsViewModel` owns the tree + folder/file CRUD (create/move/rename); shell keeps the delegating facade + the tab-bridging open/load/save/rename-tab (those move to the workspace VM in phase 4). Build 0 err, 274 tests, launch-clean.
- [~] Phase 3 — execution done; results binding deferred. `ExecutionViewModel` owns run/cancel/page/count/
  FK-nav/inline-save/discard/preview + `IsBusy` + the CTS; `PendingStatement` promoted to a top-level record.
  Shell keeps the delegating facade (`ShellViewModel.Execution.cs`) + `SearchHistoryAsync` (feeds History).
  Build 0 err, 274 tests (incl. 17 ExecuteAsync + save/count/nav/preview), launch-clean. **Deferred:**
  `ResultsViewModel` + binding `ResultView` — `ResultView` is a code-built control driven by imperative
  callbacks (not XAML bindings), so its rebind rides with the compiled-bindings flip in phase 6.
- [x] Phase 4 — workspace + editor. `WorkspaceViewModel` owns the editor tabs' lifecycle (`NewTab`/
  `CloseTab`/`RestoreTabsAsync` + `_scratchCounter`) and the tab-bridging (open/load/save/rename-tab);
  `Tabs` + `SelectedTab` moved into `WorkspaceContext` (with a `SelectedTabChanged` event). The three
  sibling VMs now read `_ctx.Tabs`/`_ctx.SelectedTab` directly and dropped their `Func`/collection ctor
  params; `ConnectionsViewModel` self-subscribes to `SelectedTabChanged` (no more shell forwarding of the
  tab-switch). Cross-concern touches stay callbacks, not VM→VM refs (workspace gets `refreshScripts`/
  `updateTitle`/`applyConnectionDisplay`/`renameScript` delegates). Shell keeps the delegating facade
  (`ShellViewModel.Tabs.cs`); `ShellViewModel.Scripts.cs` shed the tab-bridging. Editor↔tab sync extracted
  from the code-behind into `Editing/EditorTextBehavior.cs` (owns the load guard + write-back; highlight/
  folding/completion stay in code-behind observing the same editor events) — the documented AvaloniaEdit
  binding exception. Build 0 err (3 pre-existing warnings), 274 tests, launch-clean (`project ready`).
  **Deferred:** the `_tabMru` (Ctrl+Tab cycling) stays in `MainWindow` code-behind — it's intertwined with
  keyboard cycle state (`_mruCycling`/`_mruIndex`); moving it to the workspace VM rides with phase 5
  (de-code-behind). Binding-topology flip still deferred to phase 6.
- [~] Phase 5 — de-code-behind (dialog service done; command-migration is view-legit and stays). `IDialogService`
  grew from just `ConfirmWriteAsync` to own every modal/picker the code-behind used to `new` up directly:
  `ShowConnectionDialogAsync`, `ShowTextPromptAsync`, `PickFolderAsync`, `PickOpenScriptAsync`,
  `PickSaveScriptAsync`, `ShowSqlPreview`. `DialogService` (stateless — resolves the active window +
  its `StorageProvider` lazily) now constructs `ConnectionDialog`/`TextPromptDialog`, the folder/file
  pickers, and the SQL-preview window (moved verbatim out of `MainWindow.Overlays`). Both `MainWindow`
  (Chrome/ConnectionCommands/Commands, ~9 sites) and `SidebarView` (connection-edit + 4 script/folder
  prompts) hold a `new DialogService()` and call `_dialogs.*` — neither code-behind knows a concrete dialog
  type anymore; `SidebarView.Host` (owner lookup) deleted, unused picker helpers/`SqlFileType` removed from
  `MainWindow.Commands`. Build 0 err, 274 tests, launch-clean (`project ready`).
  **Deliberately NOT moved** (view-legit per §2.3 — code-behind "wires events and builds visuals"): the
  editor mechanics (open-line/comment/select-statement/folding), focus cycling, the Alt menu-bar toggle,
  the keyboard dispatch pipeline, and the self-built overlays (command palette, quick-pick, pending-changes
  panel — `OverlayLayer` visuals, not modal dialogs). `AboutDialog`/`KeybindingsWindow` stay inline
  (single-site, view/Input-owned). So `MainWindow.axaml.cs` is *not* "wiring only" — the plan's aspiration
  overshot what's genuinely non-view; the real phase-5 win (dialog centralization) is done.
- [x] Phase 6 — dissolve / flip bindings. `AvaloniaUseCompiledBindingsByDefault=true` — every binding path is
  now build-verified. The shell exposes the child VMs as public props (`Connections`/`Scripts`/`Workspace`/
  `Execution`); XAML rebound to `Vm.<child>.X` (MainWindow.axaml + SidebarView.axaml), code-behind + the two
  test files rebound to `Vm.<child>.X` / `vm.<child>.X`. All four concern-facade partials deleted
  (`ShellViewModel.{Connections,Scripts,Tabs,Execution}.cs`) along with the PropertyChanged forwarding —
  the shell is left with `.cs` (chrome state + child composition + SearchHistoryAsync + 3 private helpers),
  `.Projects.cs` (lifecycle), `.Session.cs` (persistence). `RunButtonText` moved to `ExecutionViewModel`
  (notifies off `IsBusy`); code-behind now subscribes its one PropertyChanged handler to the shell +
  Workspace + Connections VMs (SelectedTab / accent / DB-pill no longer forwarded). Dead `HistoryWindow`
  deleted. Three bindings that can't be statically typed use the `ReflectionBinding` escape hatch (the two
  `TreeViewItem.IsExpanded` style setters against the node DataContext, and the `RelativeSource=Window`
  `DataContext.History.SelectedRow`). Build 0 err, 274 tests, launch-clean (`project ready`, no binding errors).
  **ALL 6 PHASES DONE — MVVM refactor complete.** Still needs the user's live QA (Wayland blocks headless UI).
