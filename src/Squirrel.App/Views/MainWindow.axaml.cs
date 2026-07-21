using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.TextMate;
using Squirrel.App.Completion;
using Squirrel.App.Editing;
using Squirrel.App.Input;
using Squirrel.App.ViewModels;
using Squirrel.Core.Data;
using Squirrel.Sql;
using TextMateSharp.Grammars;

namespace Squirrel.App.Views;

public partial class MainWindow : Window
{
    private readonly CompletionController _completion;
    private readonly SqlFoldingController _folding;
    private readonly StatementMargin _statementHighlight = new();
    private readonly CommandRegistry _commands = new();
    private readonly KeyDispatcher _dispatcher;
    private readonly System.Collections.Generic.IReadOnlyList<string> _keymapWarnings;
    private bool _keymapWarningsShown;
    private readonly MruList<EditorTabViewModel> _tabMru = new();
    private bool _mruCycling;   // true while Ctrl is held during a Ctrl+Tab cycle
    private int _mruIndex;
    private System.Collections.Generic.HashSet<string> _navCommands = new();
    private bool _loadingEditor;          // guards editor<->tab sync while swapping tabs
    private bool _suppressProjectChange;   // guards the project combo during programmatic updates

    public MainWindow()
    {
        App.LogStartup("MainWindow ctor start");
        InitializeComponent();
        App.LogStartup("XAML loaded");
        ApplyEditorChrome(Editor);
        InstallSqlHighlighting();
        App.LogStartup("TextMate installed");

        _completion = new CompletionController(Editor, new CompletionEngine(), () => Vm?.SnapshotForSelectedTab());
        _folding = new SqlFoldingController(Editor); // installs the fold margin (left of the text)
        Editor.TextArea.LeftMargins.Add(_statementHighlight); // its own column, right of the line numbers

        // One keybinding pipeline for the whole app: the registry holds command delegates, the keymap
        // maps gestures to command ids, the dispatcher resolves keystrokes per scope. Global + Editor
        // commands register here; the results grid registers its own into the shared registry.
        RegisterCommands(_commands);
        // user keybindings.json layered over defaults; pass the registered ids so config can bind
        // palette-only commands (grid commands all have defaults, so they're known either way).
        var keymap = KeymapLoader.LoadFromConfig(KeymapDefaults.Build(), _commands.All.Select(c => c.Id).ToHashSet());
        _dispatcher = new KeyDispatcher(keymap.Keymap, _commands);
        _keymapWarnings = keymap.Warnings;
        ResultsView.CommandDispatcher = _dispatcher;
        SyncMenuGestures();

        // Claim navigation keys (tab switching, focus, pickers) in the tunnel phase so the framework's
        // tab traversal and the editor/grid don't consume them first.
        AddHandler(KeyDownEvent, OnWindowNavKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // The Alt-toggled menu behaves like a real menu bar: auto-hide on a click outside it or once a
        // (leaf) menu item is invoked.
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        MainMenu.AddHandler(MenuItem.ClickEvent, OnMenuItemInvoked);

        // Editor-editing shortcuts must pre-empt AvaloniaEdit, which consumes Enter/'/'/brackets on
        // its own KeyDown — so handle them during the tunnel phase, before the editor sees them.
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // The sidebar (Connections/Scripts/History) is its own control; it owns its dialogs and tree
        // interactions and hands back the three actions the shell still owns.
        Sidebar.AddConnectionRequested = () => _ = AddConnectionAsync();
        Sidebar.EditorSyncRequested = LoadEditorFromSelectedTab;
        Sidebar.SqlPreviewRequested = ShowSqlPreview;

        // Stacked/Tabbed toggle persists on the VM (which round-trips it into the session).
        ResultsView.ViewModeChanged = mode => { if (Vm is not null) Vm.ResultsViewMode = mode; };

        // Paging footer buttons call back into the shell VM (Vm is resolved lazily at click time).
        ResultsView.LoadMore = rs => Vm?.LoadMoreAsync(rs) ?? Task.CompletedTask;
        ResultsView.CountTotal = rs => Vm?.CountTotalAsync(rs) ?? Task.CompletedTask;
        ResultsView.NavigateForeignKey = async (rs, col, row) =>
        {
            if (Vm is null) return;
            await Vm.NavigateForeignKeyAsync(rs, col, row); // runs inline, stacks the prior result
            RebuildResults(Vm.SelectedTab);
        };
        ResultsView.GoBack = () =>
        {
            Vm?.SelectedTab?.GoBack();
            RebuildResults(Vm?.SelectedTab);
        };
        ResultsView.SaveChanges = async rs =>
        {
            if (Vm is null) return;
            await Vm.SaveChangesAsync(rs);      // applies in one tx, updating affected rows in place
            ResultsView.RefreshRowHighlights(); // clear the pending tints (no full rebuild → scroll kept)
        };
        ResultsView.DiscardChanges = async rs =>
        {
            if (Vm is null) return;
            await Vm.DiscardChangesAsync(rs);   // reverts pending changes in place
            RebuildResults(Vm.SelectedTab);     // re-render the restored rows
        };
        ResultsView.PreviewSql = ShowPendingScript; // floating color-coded script panel (design §5)

        // Translucent selection so syntax-highlighted glyphs stay readable through it — the opaque
        // default paints solid over the colored text. Kanagawa wave-blue at ~40% alpha.
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x2D, 0x4F, 0x67));

        Editor.TextChanged += (_, _) =>
        {
            if (!_loadingEditor && Vm?.SelectedTab is { } tab) tab.Text = Editor.Text;
            UpdateStatementHighlight();
            _folding.Refresh();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (!_loadingEditor && Vm?.SelectedTab is { } tab) tab.CaretOffset = Editor.CaretOffset;
            UpdateStatementHighlight();
        };
        Editor.TextArea.SelectionChanged += (_, _) => UpdateStatementHighlight();

        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) => HookViewModel();

        SetResultsVisible(false); // no results yet → editor fills; the pane appears on the first run
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    // Remembered editor/results split proportions, so hiding then re-showing the results pane (Ctrl+R)
    // restores the user's dragged sizes rather than snapping back to the 2:3 default.
    private GridLength _savedEditorRow = new(2, GridUnitType.Star);
    private GridLength _savedResultsRow = new(3, GridUnitType.Star);

    /// <summary>Show or collapse the results pane (grid row + splitter). When collapsed the editor
    /// fills the whole workspace; there's no empty split. Called on every run/tab-switch and by Ctrl+R.</summary>
    private void SetResultsVisible(bool visible)
    {
        var rows = WorkspaceGrid.RowDefinitions;
        if (visible)
        {
            rows[0].Height = _savedEditorRow;
            rows[1].Height = GridLength.Auto;
            rows[2].Height = _savedResultsRow;
        }
        else
        {
            if (rows[2].Height.Value > 0) { _savedEditorRow = rows[0].Height; _savedResultsRow = rows[2].Height; }
            rows[0].Height = new GridLength(1, GridUnitType.Star); // editor fills
            rows[1].Height = new GridLength(0);
            rows[2].Height = new GridLength(0);
        }
        ResultsSplitter.IsVisible = visible;
        ResultsView.IsVisible = visible;
    }

    /// <summary>Ctrl+R: toggle the results pane, but only when there's actually a result to show. Hiding
    /// the pane drops focus back to the editor (it may have been in the now-collapsed grid).</summary>
    private void ToggleResultsVisible()
    {
        if (ResultsView.Results is not { Count: > 0 }) return;
        var show = !ResultsView.IsVisible;
        SetResultsVisible(show);
        if (!show) Editor.TextArea.Focus();
    }

    private void HookViewModel()
    {
        if (Vm is null) return;
        Vm.ConfirmDangerousWrite = ConfirmDangerousWriteAsync;
        Vm.PropertyChanged -= OnViewModelPropertyChanged;
        Vm.PropertyChanged += OnViewModelPropertyChanged;
        Vm.TabDatabases.CollectionChanged -= OnTabDatabasesChanged;
        Vm.TabDatabases.CollectionChanged += OnTabDatabasesChanged;
        ResultsView.ViewMode = Vm.ResultsViewMode; // seed before the first results render
        LoadEditorFromSelectedTab();
        SyncProjectCombo();
        SyncDbPicker();
        App.SetConnectionAccent(Vm.ActiveConnectionColor); // seed the accent for the initial tab
        _tabMru.Sync(Vm.Tabs);
        if (Vm.SelectedTab is { } seedTab) _tabMru.Use(seedTab);

        // Surface any keybindings.json problems once, in the status bar (non-fatal — defaults still applied).
        if (!_keymapWarningsShown && _keymapWarnings.Count > 0)
        {
            _keymapWarningsShown = true;
            Vm.StatusText = _keymapWarnings.Count == 1
                ? _keymapWarnings[0]
                : $"{_keymapWarnings.Count} keybinding issues — {_keymapWarnings[0]}";
        }
    }

    private void OnTabDatabasesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => SyncDbPicker();

    /// <summary>Apply the (cheap) editor chrome synchronously so first paint is already dark; the
    /// (expensive) TextMate grammar registry is installed later via <see cref="InstallSqlHighlighting"/>.</summary>
    private void ApplyEditorChrome(AvaloniaEdit.TextEditor editor)
    {
        // Kanagawa surface (#1F1F28), current-line highlight (#252535), faint line numbers (#54546D).
        editor.Background = ThemeBrush("Bg.Editor");
        editor.LineNumbersForeground = ThemeBrush("Text.Faint");
        editor.Options.HighlightCurrentLine = true;
        var lineActive = ((SolidColorBrush)ThemeBrush("Bg.LineActive")).Color;
        editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(lineActive);
        editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(lineActive)); // no contrasting box
    }

    /// <summary>Install TextMate SQL syntax highlighting. Deferred off first paint — building the
    /// grammar/theme registry is ~100ms+ and the editor renders plain (already dark) until it lands.
    /// DarkPlus supplies token colors; exact Kanagawa hues are deferred (needs internal TextMateSharp
    /// APIs — docs/design/editor-4a/README.md §Fidelity).</summary>
    private void InstallSqlHighlighting()
    {
        var options = new RegistryOptions(ThemeName.DarkPlus);
        var installation = Editor.InstallTextMate(options);
        var sql = options.GetLanguageByExtension(".sql");
        if (sql is not null)
            installation.SetGrammar(options.GetScopeByLanguageId(sql.Id));
    }

    /// <summary>Resolve a token brush from app resources (falls back to transparent if missing).</summary>
    private IBrush ThemeBrush(string key)
        => (Application.Current?.FindResource(key) as IBrush) ?? Brushes.Transparent;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
        {
            LoadEditorFromSelectedTab();
            // Promote on a normal switch, but not while a Ctrl+Tab cycle is in flight (that commits on release).
            if (!_mruCycling && Vm?.SelectedTab is { } t) _tabMru.Use(t);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.ActiveConnectionColor))
            App.SetConnectionAccent(Vm?.ActiveConnectionColor); // recolor tab accent, dots, results, status line
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedTabDatabase))
            SyncDbPicker();
        else if (e.PropertyName == nameof(MainWindowViewModel.ResultsViewMode))
            ResultsView.ViewMode = Vm?.ResultsViewMode ?? Squirrel.Core.Workspace.ResultsViewMode.Stacked;
        else if (e.PropertyName is nameof(MainWindowViewModel.Title) or nameof(MainWindowViewModel.ProjectDirectory))
            SyncProjectCombo();
    }

    private void LoadEditorFromSelectedTab()
    {
        var tab = Vm?.SelectedTab;
        _loadingEditor = true;
        Editor.Text = tab?.Text ?? "";
        if (tab is not null)
            Editor.CaretOffset = System.Math.Clamp(tab.CaretOffset, 0, Editor.Text.Length);
        _loadingEditor = false;
        RebuildResults(tab);
        UpdateStatementHighlight();
    }

    /// <summary>Mark the statement Run will execute — the selection if any, else the statement at
    /// the caret — so the highlight always matches <see cref="RunAsync"/>.</summary>
    private void UpdateStatementHighlight()
    {
        if (!string.IsNullOrEmpty(Editor.SelectedText))
            _statementHighlight.SetSpan(-1, -1); // selection is its own indicator
        else if (Squirrel.Sql.StatementSplitter.StatementAt(Editor.Text, Editor.CaretOffset) is { } stmt)
            _statementHighlight.SetSpan(stmt.TrimmedStart, stmt.TrimmedEnd);
        else
            _statementHighlight.SetSpan(-1, -1);
    }

    /// <summary>Alt+Up / Alt+Down: move the caret to the previous / next runnable statement.</summary>
    private void MoveToAdjacentStatement(int direction)
    {
        var text = Editor.Text;
        var spans = Squirrel.Sql.StatementSplitter.Split(text)
            .Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (spans.Count == 0) return;

        var current = Squirrel.Sql.StatementSplitter.StatementAt(text, Editor.CaretOffset);
        var idx = current is null ? 0 : spans.FindIndex(s => s.Start == current.Start);
        if (idx < 0) idx = 0;

        var target = System.Math.Clamp(idx + direction, 0, spans.Count - 1);
        Editor.CaretOffset = spans[target].TrimmedStart;
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void SyncProjectCombo()
    {
        if (Vm?.ProjectDirectory is not { } dir) return;
        _suppressProjectChange = true;
        ProjectCombo.SelectedItem = Vm.RecentProjects.FirstOrDefault(r => r.Directory == dir);
        _suppressProjectChange = false;
    }

}
