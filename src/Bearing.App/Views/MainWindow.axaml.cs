using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bearing.App.Completion;
using Bearing.App.Controls;
using Bearing.App.Editing;
using Bearing.App.Input;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.Sql;

namespace Bearing.App.Views;

public partial class MainWindow : Window
{
    private readonly CompletionController _completion;
    private readonly SqlFoldingController _folding;
    private readonly EditorTextCommands _text;              // statement-aware editor ops + Run's SQL
    private readonly EditorTextBehavior _editorText;        // editor <-> SelectedTab buffer/caret sync
    private readonly CommandRegistry _commands = new();
    private readonly KeyDispatcher _dispatcher;
    private readonly CommandPaletteHost _palette;           // command palette + quick-pick overlays
    private readonly TabNavigator _tabs;                    // visual / MRU / go-to-N tab switching
    private readonly ResultsPaneController _resultsPane;    // editor / results split visibility
    private readonly IReadOnlyList<string> _keymapWarnings;
    private bool _keymapWarningsShown;
    private HashSet<string> _navCommands = new();
    private readonly Bearing.App.Services.IDialogService _dialogs = new DialogService(); // owns dialog/picker construction
    private bool _suppressProjectChange;   // guards the project combo during programmatic updates
    private CompletionToastHost? _toasts;  // built once the window is open (its manager needs a top level)
    private bool _closeConfirmed;          // set once the quit-while-running prompt has been answered "yes"

    public MainWindow()
    {
        App.LogStartup("MainWindow ctor start");
        InitializeComponent();
        App.LogStartup("XAML loaded");
        // Cheap chrome first so first paint is already dark; the expensive TextMate registry follows.
        EditorChrome.Apply(Editor);
        EditorChrome.InstallSqlHighlighting(Editor);
        App.LogStartup("TextMate installed");

        _completion = new CompletionController(Editor, new CompletionEngine(), () => Vm?.Execution.SnapshotForSelectedTab());
        _folding = new SqlFoldingController(Editor); // installs the fold margin (left of the text)
        _text = new EditorTextCommands(Editor);      // installs the statement-highlight margin

        // These read the keymap lazily: the shortcuts editor can replace it at runtime, and they are built
        // before the dispatcher exists (commands must be registered first).
        // The null-forgiving `!` is the ordering, not a real nullable: both lambdas run long after the
        // ctor has assigned _dispatcher, but they are created before it.
        _tabs = new TabNavigator(() => _dispatcher!.Keymap);
        _palette = new CommandPaletteHost(this, _commands, () => _dispatcher!.Keymap);
        _resultsPane = new ResultsPaneController(WorkspaceGrid, ResultsSplitter, ResultsView);

        // One keybinding pipeline for the whole app: the registry holds command delegates, the keymap
        // maps gestures to command ids, the dispatcher resolves keystrokes per scope. Global + Editor
        // commands register here; the results grid registers its own into the shared registry.
        RegisterCommands(_commands);
        // The grid's commands register here too, before the keymap is read: the ones that ship unbound
        // (Copy as ▸, Export ▸, Fetch all) are only bindable from keybindings.json if the loader has already
        // seen their ids, and it takes them from the registry.
        ResultsView.RegisterGridCommands(_commands);
        // user keybindings.json layered over defaults; pass the registered ids so config can bind
        // commands that ship unbound (palette-only).
        var keymap = KeymapLoader.LoadFromConfig(KeymapDefaults.Build(), _commands.All.Select(c => c.Id).ToHashSet());
        _dispatcher = new KeyDispatcher(keymap.Keymap, _commands);
        _keymapWarnings = keymap.Warnings;
        ResultsView.CommandDispatcher = _dispatcher;
        SyncMenuGestures();

        InstallWindowHandlers();
        WireSidebar();
        WireResultsView();

        // Editor buffer/caret ↔ SelectedTab sync (the documented AvaloniaEdit binding exception); this owns
        // the load guard + write-back. The highlight/folding hooks below observe the same editor events.
        _editorText = new EditorTextBehavior(Editor);
        Editor.TextChanged += (_, _) => { _text.UpdateStatementHighlight(); _folding.Refresh(); };
        Editor.TextArea.Caret.PositionChanged += (_, _) => _text.UpdateStatementHighlight();
        Editor.TextArea.SelectionChanged += (_, _) => _text.UpdateStatementHighlight();

        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) => HookViewModel();

        _resultsPane.SetVisible(false); // no results yet → editor fills; the pane appears on the first run
    }

    private ShellViewModel? Vm => DataContext as ShellViewModel;

    /// <summary>Window-level key/pointer handlers that must pre-empt the controls beneath them.</summary>
    private void InstallWindowHandlers()
    {
        // Claim navigation keys (tab switching, focus, pickers) in the tunnel phase so the framework's
        // tab traversal and the editor/grid don't consume them first.
        AddHandler(KeyDownEvent, OnWindowNavKey, RoutingStrategies.Tunnel);

        // Escape must cancel a running query no matter where focus sits (editor, results grid, Run button).
        // The grid's own Escape (clear selection) and AvaloniaEdit both sit below the window in the tunnel,
        // so claim it here first — see OnWindowEscapeCancel for the more-modal exceptions.
        AddHandler(KeyDownEvent, OnWindowEscapeCancel, RoutingStrategies.Tunnel);

        // The Alt-toggled menu behaves like a real menu bar: auto-hide on a click outside it or once a
        // (leaf) menu item is invoked.
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        MainMenu.AddHandler(MenuItem.ClickEvent, OnMenuItemInvoked);

        // Editor-editing shortcuts must pre-empt AvaloniaEdit, which consumes Enter/'/'/brackets on
        // its own KeyDown — so handle them during the tunnel phase, before the editor sees them.
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The sidebar (Connections/Scripts/History) is its own control; it owns its dialogs and tree
    /// interactions and hands back the three actions the shell still owns.</summary>
    private void WireSidebar()
    {
        Sidebar.AddConnectionRequested = () => _ = AddConnectionAsync();
        Sidebar.EditorSyncRequested = LoadEditorFromSelectedTab;
        Sidebar.SqlPreviewRequested = _dialogs.ShowSqlPreview;
    }

    /// <summary>Bridge the results dock's callbacks to the shell view-model. <c>Vm</c> is resolved lazily at
    /// invoke time — these are wired before a DataContext exists.</summary>
    private void WireResultsView()
    {
        // Stacked/Tabbed toggle persists on the VM (which round-trips it into the session).
        ResultsView.ViewModeChanged = mode => { if (Vm is not null) Vm.ResultsViewMode = mode; };

        ResultsView.LoadMore = rs => Vm?.Execution.LoadMoreAsync(rs) ?? Task.CompletedTask;
        ResultsView.CountTotal = rs => Vm?.Execution.CountTotalAsync(rs) ?? Task.CompletedTask;
        ResultsView.FetchAll = async rs => { if (Vm is not null) await Vm.Execution.FetchAllAsync(rs); };
        ResultsView.Export = (rs, format) => Vm?.Execution.ExportAsync(rs, format) ?? Task.CompletedTask;
        ResultsView.NavigateForeignKey = async (rs, col, row) =>
        {
            if (Vm is null) return;
            await Vm.Execution.NavigateForeignKeyAsync(rs, col, row); // runs inline, stacks the prior result
            RebuildResults(Vm.Workspace.SelectedTab);
        };
        ResultsView.GoBack = () =>
        {
            Vm?.Workspace.SelectedTab?.GoBack();
            RebuildResults(Vm?.Workspace.SelectedTab);
        };
        ResultsView.SaveChanges = async rs =>
        {
            if (Vm is null) return;
            await Vm.Execution.SaveChangesAsync(rs);      // applies in one tx, updating affected rows in place
            ResultsView.RefreshRowHighlights();           // clear the pending tints (no full rebuild → scroll kept)
        };
        ResultsView.DiscardChanges = async rs =>
        {
            if (Vm is null) return;
            await Vm.Execution.DiscardChangesAsync(rs);   // reverts pending changes in place
            RebuildResults(Vm.Workspace.SelectedTab);     // re-render the restored rows
        };
    }

    private void HookViewModel()
    {
        if (Vm is null) return;
        // Shell chrome (Title/ProjectDirectory/ResultsViewMode) fires on the shell; SelectedTab on the
        // workspace VM; the accent + DB pill on the connections VM — subscribe the one handler to all three
        // (bindings now point straight at the child VMs, so the shell no longer forwards their changes).
        Vm.PropertyChanged -= OnViewModelPropertyChanged;
        Vm.PropertyChanged += OnViewModelPropertyChanged;
        Vm.Workspace.PropertyChanged -= OnViewModelPropertyChanged;
        Vm.Workspace.PropertyChanged += OnViewModelPropertyChanged;
        Vm.Connections.PropertyChanged -= OnViewModelPropertyChanged;
        Vm.Connections.PropertyChanged += OnViewModelPropertyChanged;
        Vm.Connections.TabDatabases.CollectionChanged -= OnTabDatabasesChanged;
        Vm.Connections.TabDatabases.CollectionChanged += OnTabDatabasesChanged;
        // A run that finishes on a tab the user isn't looking at can't use the status bar (that describes
        // the tab on screen), so it toasts instead — the only notification sink in the app.
        Vm.Execution.BackgroundCompleted -= OnBackgroundCompleted;
        Vm.Execution.BackgroundCompleted += OnBackgroundCompleted;
        // A finished export toasts too: the status line can't carry the one action anyone wants next, which
        // is "show me the file".
        Vm.Execution.ExportCompleted -= OnExportCompleted;
        Vm.Execution.ExportCompleted += OnExportCompleted;
        ResultsView.ViewMode = Vm.ResultsViewMode; // seed before the first results render
        LoadEditorFromSelectedTab();
        SyncProjectCombo();
        SyncDbPicker();
        App.SetConnectionAccent(Vm.Connections.ActiveConnectionColor); // seed the accent for the initial tab
        _tabs.Sync(Vm.Workspace.Tabs);
        if (Vm.Workspace.SelectedTab is { } seedTab) _tabs.Promote(seedTab);

        // Surface any keybindings.json problems once, in the status bar (non-fatal — defaults still applied).
        if (!_keymapWarningsShown && _keymapWarnings.Count > 0)
        {
            _keymapWarningsShown = true;
            Vm.StatusText = _keymapWarnings.Count == 1
                ? _keymapWarnings[0]
                : $"{_keymapWarnings.Count} keybinding issues — {_keymapWarnings[0]}";
        }
    }

    // NEVER assign the picker's selection from inside this notification. The ComboBox's own
    // ItemsSourceView subscribes to TabDatabases after this handler does, so mid-notification it still
    // reports the pre-change items while the backing list is already mutated: assigning SelectedItem then
    // resolves a stale index and blows up enumerating the selection (ArgumentOutOfRangeException out of
    // SelectionModel). Post instead — one sync once the whole rebuild (Clear + N adds) has settled.
    private bool _dbSyncQueued;

    private void OnTabDatabasesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_dbSyncQueued) return;
        _dbSyncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _dbSyncQueued = false;
            SyncDbPicker();
        });
    }

    /// <summary>Build the notification sink as soon as the window is on screen (its manager attaches to the
    /// top level, so it can't exist earlier). Deliberately not deferred to the first completion: a host
    /// constructed and shown in one turn drops that first toast (see <see cref="CompletionToastHost"/>).</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _toasts ??= new CompletionToastHost(this, RevealCompletedTab);
    }

    /// <summary>Toast a run that finished off screen. Marshalled: the completion is raised from whatever
    /// thread the query's continuation landed on.</summary>
    private void OnBackgroundCompleted(BackgroundCompletion completion)
        => Dispatcher.UIThread.Post(() =>
            (_toasts ??= new CompletionToastHost(this, RevealCompletedTab)).Show(completion));

    /// <summary>Toast a finished export, with the containing folder one click away. Marshalled: the write ran
    /// on a thread-pool thread.</summary>
    private void OnExportCompleted(ExportCompletion export)
        => Dispatcher.UIThread.Post(() =>
            (_toasts ??= new CompletionToastHost(this, RevealCompletedTab)).Show(
                title: "Export complete",
                message: $"{export.RowCount:N0} rows → {System.IO.Path.GetFileName(export.Path)}",
                clickHint: "Click to open the folder.",
                onClick: () => FileReveal.OpenContainingFolder(export.Path),
                // Unlike a query completion, this one is redundant the moment it's read — the file is already
                // on disk and the status bar says so too — so it doesn't sit there until dismissed.
                expiration: TimeSpan.FromSeconds(10)));

    /// <summary>Clicking a completion toast goes to the query that finished: the view-model switches project
    /// if the tab is parked in another one and selects it, and the window brings itself forward.</summary>
    private void RevealCompletedTab(BackgroundCompletion completion)
    {
        if (Vm is not { } vm || completion.Tab is not { } tab) return;
        CrashReporter.Observe(Reveal(), "toast.reveal-tab");

        async Task Reveal()
        {
            await vm.RevealTabAsync(tab);
            Activate();
            Editor.TextArea.Focus();
        }
    }

    /// <summary>
    /// Quitting is the one action that can throw away a query still in flight — tab and project switches
    /// both leave it running — so ask before letting the close proceed.
    /// <para>
    /// The block path deliberately does <b>not</b> call <c>base</c>: that is what raises <c>Closing</c>, and
    /// the handlers on it save the session and dispose every live connection. Running them for a close that
    /// isn't happening would kill the very queries the user just chose to keep.
    /// </para>
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var running = QuitGuard.RunningCount(Vm);
        if (!_closeConfirmed && running > 0 && Vm is { } vm)
        {
            e.Cancel = true;
            _ = ConfirmQuitAsync(vm, running);
            return;
        }
        base.OnClosing(e);
    }

    private async Task ConfirmQuitAsync(ShellViewModel vm, int running)
    {
        if (!await QuitGuard.ConfirmAsync(vm, _dialogs, running)) return;
        _closeConfirmed = true;
        Close();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.SelectedTab))
        {
            LoadEditorFromSelectedTab();
            // Promote on a normal switch; TabNavigator ignores this while a Ctrl+Tab cycle is in flight
            // (that commits on modifier release).
            if (Vm?.Workspace.SelectedTab is { } t) _tabs.Promote(t);
        }
        else if (e.PropertyName == nameof(ConnectionsViewModel.ActiveConnectionColor))
            App.SetConnectionAccent(Vm?.Connections.ActiveConnectionColor); // recolor tab accent, dots, results, status line
        else if (e.PropertyName == nameof(ConnectionsViewModel.SelectedTabDatabase))
            SyncDbPicker();
        else if (e.PropertyName == nameof(ShellViewModel.ResultsViewMode))
            ResultsView.ViewMode = Vm?.ResultsViewMode ?? Bearing.Core.Workspace.ResultsViewMode.Stacked;
        else if (e.PropertyName is nameof(ShellViewModel.Title) or nameof(ShellViewModel.ProjectDirectory))
            SyncProjectCombo();
    }

    private void LoadEditorFromSelectedTab()
    {
        var tab = Vm?.Workspace.SelectedTab;
        _editorText.Bind(tab);   // pushes text/caret into the editor under the load guard
        RebuildResults(tab);
        _text.UpdateStatementHighlight();
    }

    private void SyncProjectCombo()
    {
        if (Vm?.ProjectDirectory is not { } dir) return;
        _suppressProjectChange = true;
        ProjectCombo.SelectedItem = Vm.RecentProjects.FirstOrDefault(r => r.Directory == dir);
        _suppressProjectChange = false;
    }
}
