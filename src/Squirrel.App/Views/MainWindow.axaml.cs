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

        // Same trick for the connections tree: intercept Up/Down/Esc/Backspace before the TreeView's
        // built-in node navigation, so a search cycles matches instead of walking every row.
        SchemaTree.AddHandler(KeyDownEvent, OnSchemaTreeKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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
        Vm.History.PropertyChanged -= OnHistoryPropertyChanged;
        Vm.History.PropertyChanged += OnHistoryPropertyChanged;
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

    private void OnHistoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.HistoryPanelViewModel.SelectedRow)) UpdateHistoryPreviewRow();
    }

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

    // ---- tabs ----

    private void OnNewTabClick(object? sender, RoutedEventArgs e) => Vm?.NewTab();

    private void OnCloseTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: EditorTabViewModel tab }) { Vm?.CloseTab(tab); e.Handled = true; }
    }

    private async void OnTabHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: EditorTabViewModel tab }) await RenameTabAsync(tab);
    }

    private async Task RenameTabAsync(EditorTabViewModel tab)
    {
        if (Vm is null) return;
        var current = tab.IsScratch ? tab.DisplayName : tab.Header;
        var prompt = new TextPromptDialog(tab.IsScratch ? "Rename tab" : "Rename script file", current);
        var name = await prompt.ShowDialog<string?>(this);
        if (name is not null) await Vm.RenameTabAsync(tab, name);
    }

    // ---- side pane ----

    private void OnToggleSidePane(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.SidePaneOpen = !Vm.SidePaneOpen;
    }

    // ---- connections ----

    private async void OnAddConnectionClick(object? sender, RoutedEventArgs e) => await AddConnectionAsync();

    private async void OnEditKeybindingsClick(object? sender, RoutedEventArgs e) => await EditKeybindingsAsync();

    /// <summary>settings.keybindings: edit the keymap, then persist the minimal diff, apply it live to the
    /// dispatcher, and refresh the menu gestures.</summary>
    private async Task EditKeybindingsAsync()
    {
        var defaults = KeymapDefaults.Build();
        var edited = await new KeybindingsWindow(_dispatcher.Keymap, defaults, _commands.All).ShowDialog<Keymap?>(this);
        if (edited is null) return;
        KeymapLoader.SaveOverrides(KeymapDiff.ComputeOverrides(defaults, edited.Bindings));
        _dispatcher.Keymap = edited;   // ResultView shares this dispatcher, so the grid picks it up too
        SyncMenuGestures();
        if (Vm is not null) Vm.StatusText = "Keyboard shortcuts updated.";
    }

    /// <summary>connection.new: open the connection dialog for a brand-new connection.</summary>
    private async Task AddConnectionAsync()
    {
        if (Vm is null) return;
        var dialog = new ConnectionDialog(null, null, (i, p, ct) => Vm.TestConnectionAsync(i, p, ct), Vm.SecretStorageSecure);
        var result = await dialog.ShowDialog<ConnectionDialogResult?>(this);
        if (result is { Delete: false }) await Vm.AddOrUpdateConnectionAsync(result.Connection, result.Password);
    }

    /// <summary>Write-guard prompt for the VM: confirm a risky batch against a guarded connection.</summary>
    private Task<bool> ConfirmDangerousWriteAsync(
        Squirrel.Core.Data.ConnectionInfo connection, System.Collections.Generic.IReadOnlyList<string> verbs)
        => new ConfirmWriteDialog(connection, verbs).ShowDialog<bool>(this);

    /// <summary>The schema-tree node the clicked menu item / tapped row belongs to (via its DataContext).</summary>
    private static SchemaNodeViewModel? NodeOf(object? sender) => (sender as Control)?.DataContext as SchemaNodeViewModel;

    // ---- schema tree type-ahead fuzzy jump ----
    private string _treeSearch = "";

    /// <summary>Type letters to fuzzy-search the (realized) tree: highlight every match and jump the
    /// selection to the next one; repeating the same/extending text cycles through matches.</summary>
    private void OnSchemaTreeTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        _treeSearch += e.Text;
        e.Handled = true;
        ApplyTreeSearch();
    }

    private void OnSchemaTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _treeSearch.Length > 0) { ClearTreeSearch(); e.Handled = true; }
        else if (e.Key == Key.Back && _treeSearch.Length > 0)
        {
            _treeSearch = _treeSearch[..^1];
            e.Handled = true;
            if (_treeSearch.Length == 0) ClearTreeSearch(); else ApplyTreeSearch();
        }
        // While a search is active, Up/Down cycle through the highlighted matches (not every node).
        else if (_treeSearch.Length > 0 && e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            MoveToAdjacentMatch(e.Key == Key.Down ? 1 : -1);
        }
    }

    private void MoveToAdjacentMatch(int direction)
    {
        var nodes = FlattenRealized();
        var matches = nodes.Where(n => FuzzyMatch(n.Title, _treeSearch)).ToList();
        if (matches.Count == 0) return;
        var current = SchemaTree.SelectedItem as SchemaNodeViewModel;
        var idx = current is null ? -1 : matches.IndexOf(current);
        idx = idx < 0
            ? (direction > 0 ? 0 : matches.Count - 1)
            : (idx + direction + matches.Count) % matches.Count;
        SchemaTree.SelectedItem = matches[idx];
    }

    private void ClearTreeSearch()
    {
        _treeSearch = "";
        foreach (var n in FlattenRealized()) n.IsMatch = false;
        if (Vm is not null) Vm.StatusText = "";
    }

    private void ApplyTreeSearch()
    {
        var nodes = FlattenRealized();
        var matches = nodes.Where(n => FuzzyMatch(n.Title, _treeSearch)).ToList();
        foreach (var n in nodes) n.IsMatch = false;
        foreach (var m in matches) m.IsMatch = true;

        if (matches.Count == 0) { Vm!.StatusText = $"No match for “{_treeSearch}”."; return; }

        // Stay put while the current selection still matches (refining the query shouldn't jump you
        // around); otherwise land on the first match. Down/Up navigate between matches manually.
        var current = SchemaTree.SelectedItem as SchemaNodeViewModel;
        if (current is null || !FuzzyMatch(current.Title, _treeSearch))
            SchemaTree.SelectedItem = matches[0];
        Vm!.StatusText = $"“{_treeSearch}” · {matches.Count} match{(matches.Count == 1 ? "" : "es")}";
    }

    /// <summary>Depth-first list of realized (already-loaded, non-placeholder) tree nodes.</summary>
    private System.Collections.Generic.List<SchemaNodeViewModel> FlattenRealized()
    {
        var list = new System.Collections.Generic.List<SchemaNodeViewModel>();
        void Walk(System.Collections.Generic.IEnumerable<SchemaNodeViewModel> ns)
        {
            foreach (var n in ns)
            {
                if (n is MessageNodeViewModel) continue;
                list.Add(n);
                if (n.IsExpanded) Walk(n.Children);
            }
        }
        if (Vm is not null) Walk(Vm.ServerNodes);
        return list;
    }

    /// <summary>Case-insensitive subsequence (fuzzy) match: query chars appear in order in the text.</summary>
    private static bool FuzzyMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return false;
        text = text.ToLowerInvariant(); query = query.ToLowerInvariant();
        var ti = 0;
        foreach (var c in query)
        {
            ti = text.IndexOf(c, ti);
            if (ti < 0) return false;
            ti++;
        }
        return true;
    }

    private async void OnEditServer(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || NodeOf(sender) is not ServerNodeViewModel server) return;
        var existing = server.Connection;
        var password = await Vm.GetConnectionPasswordAsync(existing.Id);
        var dialog = new ConnectionDialog(existing, password, (i, p, ct) => Vm.TestConnectionAsync(i, p, ct), Vm.SecretStorageSecure);
        var result = await dialog.ShowDialog<ConnectionDialogResult?>(this);
        if (result is null) return;
        if (result.Delete) await Vm.DeleteConnectionAsync(existing.Id);
        else await Vm.AddOrUpdateConnectionAsync(result.Connection, result.Password);
    }

    private void OnUseConnectionInTab(object? sender, RoutedEventArgs e) => AssignConnectionToTab(NodeOf(sender));

    private void OnSchemaNodeDoubleTapped(object? sender, TappedEventArgs e) => AssignConnectionToTab(NodeOf(sender));

    private void AssignConnectionToTab(SchemaNodeViewModel? node)
    {
        if (Vm?.SelectedTab is { } tab && node is ServerNodeViewModel server)
            Vm.SetTabConnection(tab, server.Connection.Id);
    }

    private async void OnDeleteServer(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && NodeOf(sender) is ServerNodeViewModel server)
            await Vm.DeleteConnectionAsync(server.Connection.Id);
    }

    private async void OnRefreshServer(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && NodeOf(sender) is ServerNodeViewModel server)
            await Vm.RefreshServerMetadataAsync(server.Connection.Id);
    }

    private async void OnShowDefinition(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { CanShowDefinition: true } node) return;
        try
        {
            var definition = await node.LoadDefinitionAsync(CancellationToken.None);
            ShowSqlPreview(string.IsNullOrWhiteSpace(definition) ? "-- (no definition)" : definition, node.DefinitionTitle);
        }
        catch (System.Exception ex)
        {
            if (Vm is not null) Vm.StatusText = $"Could not load definition: {ex.Message}";
        }
    }

    private void OnCopyNodeName(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node) TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(node.Title);
    }

    // ---- scripts ----

    private static ScriptItem? ScriptOf(object? sender) => (sender as Control)?.DataContext as ScriptItem;

    private async void OnScriptActivated(object? sender, TappedEventArgs e) => await OpenScript(ScriptOf(sender));
    private async void OnOpenScriptClick(object? sender, RoutedEventArgs e) => await OpenScript(ScriptOf(sender));

    private async Task OpenScript(ScriptItem? script)
    {
        if (Vm is not null && script is not null)
        {
            await Vm.OpenScriptInNewTabAsync(script.FullPath);
            LoadEditorFromSelectedTab();
        }
    }

    private async void OnRenameScriptClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || ScriptOf(sender) is not { } script) return;
        var prompt = new TextPromptDialog("Rename script file", script.Name);
        var name = await prompt.ShowDialog<string?>(this);
        if (name is not null) await Vm.RenameScriptAsync(script.FullPath, name);
    }

    private async void OnNewScriptFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var prompt = new TextPromptDialog("New folder name", "");
        var name = await prompt.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name)) Vm.CreateScriptFolder(name);
    }

    private static ScriptFolderViewModel? FolderOf(object? sender) => (sender as Control)?.DataContext as ScriptFolderViewModel;

    private async void OnNewSubfolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || FolderOf(sender) is not { } folder) return;
        var prompt = new TextPromptDialog("New subfolder name", "");
        var name = await prompt.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name)) Vm.CreateScriptFolder(name, folder.FullPath);
    }

    private async void OnNewScriptInFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || FolderOf(sender) is not { } folder) return;
        var prompt = new TextPromptDialog("New script name", "");
        var name = await prompt.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await Vm.CreateScriptFileAsync(folder.FullPath, name) is { } path)
        {
            await Vm.OpenScriptInNewTabAsync(path);
            LoadEditorFromSelectedTab();
        }
    }

    private void OnScriptsTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ScriptsTree.SelectedItem is ScriptItem s)
        {
            e.Handled = true;
            _ = OpenScript(s);
        }
    }

    // ---- scripts drag & drop (move a script into a folder) — Avalonia 12 typed in-process transfer ----
    private static readonly DataFormat<string> ScriptPathFormat =
        DataFormat.CreateInProcessFormat<string>("squirrel.script-path");
    private Avalonia.Point _dragStart;
    private ScriptItem? _dragItem;
    private PointerPressedEventArgs? _dragPress; // DoDragDropAsync requires the originating press args

    private void OnScriptPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ScriptItem s
            && e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            _dragItem = s;
            _dragPress = e;
            _dragStart = e.GetPosition(null);
        }
    }

    private void OnScriptPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || _dragPress is null || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(null);
        if (System.Math.Abs(pos.X - _dragStart.X) <= 4 && System.Math.Abs(pos.Y - _dragStart.Y) <= 4) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(ScriptPathFormat, _dragItem.FullPath));
        var press = _dragPress;
        _dragItem = null;
        _dragPress = null;
        _ = DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move);
    }

    private void OnScriptDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(ScriptPathFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnScriptDropOnFolder(object? sender, DragEventArgs e)
    {
        if (Vm is not null && FolderOf(sender) is { } folder && e.DataTransfer.TryGetValue(ScriptPathFormat) is string src)
        {
            Vm.MoveScript(src, folder.FullPath);
            e.Handled = true;
        }
    }

    private void OnScriptDropOnRoot(object? sender, DragEventArgs e)
    {
        if (Vm?.ScriptsDirectory is { } root && e.DataTransfer.TryGetValue(ScriptPathFormat) is string src)
        {
            Vm.MoveScript(src, root);
            e.Handled = true;
        }
    }

    // ---- projects ----

    private async void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectChange || Vm is null) return;
        if (ProjectCombo.SelectedItem is RecentProjectItem item && item.Directory != Vm.ProjectDirectory)
            await Vm.OpenProjectAsync(item.Directory);
    }

    private async void OnRenameProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentProjectName is not { } current) return;
        var prompt = new TextPromptDialog("Project name", current);
        var name = await prompt.ShowDialog<string?>(this);
        if (name is not null) await Vm.RenameProjectAsync(name);
    }

    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Squirrel project folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await Vm.OpenProjectAsync(path);
    }

    private async void OnNewProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an empty folder for the new project",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            var prompt = new TextPromptDialog("Project name", new System.IO.DirectoryInfo(path).Name);
            var name = await prompt.ShowDialog<string?>(this);
            if (name is not null) await Vm.NewProjectAsync(path, name);
        }
    }

    // ---- run / open / save / history ----

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.IsBusy == true) { Vm.CancelExecution(); return; }
        await RunAsync();
    }

    // The toolbar History button now reveals the inline History side-panel (design §4) instead of a window.
    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActivePanel = SidePanel.History;
    }

    private async void OnHistorySearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is not null) { e.Handled = true; await Vm.History.ReloadAsync(CancellationToken.None); }
    }

    // Double-click a history row → open its SQL in a new tab (non-destructive; inherits the connection).
    private void OnHistoryRowActivated(object? sender, TappedEventArgs e)
    {
        if (Vm is not null && (sender as Control)?.DataContext is HistoryRowViewModel row && row.Sql.Length > 0)
        {
            Vm.NewTab(row.Sql);
            LoadEditorFromSelectedTab();
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.StatusText = "Settings — coming soon.";
    }

    // ---- database pill selection (driven in code; async ItemsSource defeats a plain binding) ----
    private bool _syncingDb;

    private void SyncDbPicker()
    {
        if (Vm is null) return;
        _syncingDb = true;
        DatabasePicker.SelectedItem = Vm.SelectedTabDatabase; // matched by value; null → placeholder
        _syncingDb = false;
    }

    private void OnDatabaseSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingDb || Vm is null) return;
        if (DatabasePicker.SelectedItem is string db) Vm.SelectedTabDatabase = db;
    }

    // ---- history preview row (real pixel row so the splitter resizes it; 0 when nothing selected) ----
    private double _historyPreviewHeight = 220;

    private void UpdateHistoryPreviewRow()
    {
        var row = HistoryGrid.RowDefinitions[2];
        if (Vm?.History.SelectedRow is not null)
        {
            row.Height = new Avalonia.Controls.GridLength(_historyPreviewHeight);
        }
        else
        {
            if (row.Height.IsAbsolute && row.Height.Value > 0)
                _historyPreviewHeight = row.Height.Value; // remember the user's drag size
            row.Height = new Avalonia.Controls.GridLength(0);
        }
    }

    // ---- side-pane resize grip ----
    private bool _resizingPane;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private void OnPaneResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null) return;
        _resizingPane = true;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = Vm.SidePaneWidth;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void OnPaneResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingPane || Vm is null) return;
        var dx = e.GetPosition(this).X - _resizeStartX;
        Vm.SidePaneWidth = System.Math.Clamp(_resizeStartWidth + dx, 180, 680);
    }

    private void OnPaneResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        _resizingPane = false;
        e.Pointer.Capture(null);
    }

    // ---- menu bar (Alt) + focus mode ----

    /// <summary>Esc unwinds, most-modal first: the menu bar → a running query.</summary>
    private bool HandleEscape()
    {
        if (Vm is null) return false;
        if (_quickPickOverlay is not null) { HideQuickPick(); return true; }
        if (_paletteOverlay is not null) { HidePalette(); return true; }
        if (_pendingScriptOverlay is not null) { HidePendingScript(); return true; }
        if (Vm.IsMenuVisible) { Vm.IsMenuVisible = false; return true; }
        if (Vm.IsBusy) { Vm.CancelExecution(); return true; }
        return false;
    }

    // Rail tile clicked: activate that panel, or collapse the pane if its tile is re-clicked while open.
    private void OnRailTileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && (sender as Control)?.Tag is string tag && System.Enum.TryParse<SidePanel>(tag, out var panel))
            Vm.ActivateOrTogglePanel(panel);
    }

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e) => await SaveAsAsync();
    private void OnCloseCurrentTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedTab is { } tab) Vm.CloseTab(tab);
    }
    private async void OnMenuRenameTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedTab is { } tab) await RenameTabAsync(tab);
    }
    private void OnMenuSchemaClick(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.ActivePanel = SidePanel.Schema; }
    private void OnMenuScriptsClick(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.ActivePanel = SidePanel.Scripts; }
    private void OnAboutClick(object? sender, RoutedEventArgs e) => AboutDialog.Open(this);

    /// <summary>
    /// Editor-scoped editing shortcuts, handled in the tunnel phase so they win over AvaloniaEdit's
    /// own handling of Enter / '/' / brackets. App-level shortcuts (Run, Save, …) stay in <see cref="OnKeyDown"/>.
    /// </summary>
    private void OnEditorKeyDown(object? sender, KeyEventArgs e) => _dispatcher.TryHandle(e, KeyScope.Editor);

    /// <summary>Set each menu item's shown gesture from the active keymap, so the menu can never drift
    /// from the real bindings (the dead Ctrl+N / Ctrl+Shift+S entries that started this overhaul).</summary>
    private void SyncMenuGestures()
    {
        MenuNewQuery.InputGesture = MenuGesture(CommandIds.TabNew);
        MenuOpen.InputGesture = MenuGesture(CommandIds.FileOpen);
        MenuSave.InputGesture = MenuGesture(CommandIds.FileSave);
        MenuSaveAs.InputGesture = MenuGesture(CommandIds.FileSaveAs);
        MenuCloseTab.InputGesture = MenuGesture(CommandIds.TabClose);
        MenuRenameTab.InputGesture = MenuGesture(CommandIds.TabRename);
        MenuToggleSidePane.InputGesture = MenuGesture(CommandIds.ViewToggleSidePane);
        MenuRun.InputGesture = MenuGesture(CommandIds.Run);
    }

    private KeyGesture? MenuGesture(string commandId)
    {
        var text = _dispatcher.Keymap.DisplayGesture(commandId);
        if (text is null) return null;
        try { return KeyGesture.Parse(text); } catch { return null; } // display-only; a physical-key binding has no KeyGesture form
    }

    /// <summary>Register every Global and Editor command. Ids and default gestures live in
    /// <see cref="KeymapDefaults"/>; this is where each id gets its behavior and applicability guard.</summary>
    private void RegisterCommands(CommandRegistry r)
    {
        // ---- Global ----
        r.Register(new KeyCommand(CommandIds.Run, "Run", KeyScope.Global, "Query", async () => await RunAsync()));
        r.Register(KeyCommand.Sync(CommandIds.CompletionTrigger, "Trigger completion", KeyScope.Global, "Editor", () => _completion.TriggerExplicit()));
        r.Register(new KeyCommand(CommandIds.FileSave, "Save", KeyScope.Global, "File", async () => await SaveAsync()));
        r.Register(new KeyCommand(CommandIds.FileSaveAs, "Save As…", KeyScope.Global, "File", async () => await SaveAsAsync()));
        r.Register(new KeyCommand(CommandIds.FileOpen, "Open…", KeyScope.Global, "File", async () => await OpenAsync()));
        r.Register(KeyCommand.Sync(CommandIds.TabNew, "New tab", KeyScope.Global, "File", () => Vm?.NewTab()));
        r.Register(KeyCommand.Sync(CommandIds.TabClose, "Close tab", KeyScope.Global, "File",
            () => { if (Vm?.SelectedTab is { } tab) Vm.CloseTab(tab); }, canRun: () => Vm?.SelectedTab is not null));
        r.Register(new KeyCommand(CommandIds.TabRename, "Rename tab…", KeyScope.Global, "File",
            async () => { if (Vm?.SelectedTab is { } tab) await RenameTabAsync(tab); }, canRun: () => Vm?.SelectedTab is not null));
        r.Register(KeyCommand.Sync(CommandIds.ViewToggleSidePane, "Toggle side pane", KeyScope.Global, "View",
            () => { if (Vm is not null) Vm.SidePaneOpen = !Vm.SidePaneOpen; }));
        r.Register(KeyCommand.Sync(CommandIds.ViewToggleResults, "Toggle results", KeyScope.Global, "View", ToggleResultsVisible));
        r.Register(KeyCommand.Sync(CommandIds.StatementPrev, "Previous statement", KeyScope.Global, "Editor", () => MoveToAdjacentStatement(-1)));
        r.Register(KeyCommand.Sync(CommandIds.StatementNext, "Next statement", KeyScope.Global, "Editor", () => MoveToAdjacentStatement(+1)));
        // Escape only claims the key when there's something to dismiss; otherwise it falls through.
        r.Register(KeyCommand.Sync(CommandIds.AppEscape, "Escape / cancel", KeyScope.Global, "View",
            () => HandleEscape(),
            canRun: () => Vm is not null && (AnyOverlayOpen || _pendingScriptOverlay is not null || Vm.IsMenuVisible || Vm.IsBusy)));
        r.Register(KeyCommand.Sync(CommandIds.PaletteOpen, "Command palette", KeyScope.Global, "View", ShowPalette));
        r.Register(KeyCommand.Sync(CommandIds.TabNext, "Next tab (visual order)", KeyScope.Global, "Tabs", () => SelectAdjacentTab(+1)));
        r.Register(KeyCommand.Sync(CommandIds.TabPrev, "Previous tab (visual order)", KeyScope.Global, "Tabs", () => SelectAdjacentTab(-1)));
        r.Register(KeyCommand.Sync(CommandIds.TabMruNext, "Next tab (recently used)", KeyScope.Global, "Tabs", () => CycleMru(+1)));
        r.Register(KeyCommand.Sync(CommandIds.TabMruPrev, "Previous tab (recently used)", KeyScope.Global, "Tabs", () => CycleMru(-1)));
        for (var n = 1; n <= 9; n++)
        {
            var i = n; // capture
            r.Register(KeyCommand.Sync(CommandIds.TabGoto(i), i == 9 ? "Go to last tab" : $"Go to tab {i}", KeyScope.Global, "Tabs", () => SelectTabByIndex(i)));
        }
        r.Register(KeyCommand.Sync(CommandIds.FocusCycle, "Cycle focus (editor / results / sidebar)", KeyScope.Global, "View", CycleFocus));
        r.Register(KeyCommand.Sync(CommandIds.FocusEditor, "Focus editor", KeyScope.Global, "View", () => Editor.TextArea.Focus()));
        r.Register(KeyCommand.Sync(CommandIds.FocusResults, "Focus results", KeyScope.Global, "View", FocusResultsPane));
        r.Register(KeyCommand.Sync(CommandIds.SelectProject, "Select project…", KeyScope.Global, "Connection", OpenProjectPicker));
        r.Register(KeyCommand.Sync(CommandIds.SelectConnection, "Select connection…", KeyScope.Global, "Connection", OpenConnectionPicker));
        r.Register(KeyCommand.Sync(CommandIds.SelectDatabase, "Select database…", KeyScope.Global, "Connection", OpenDatabasePicker));
        r.Register(KeyCommand.Sync(CommandIds.PanelConnections, "Show Connections panel", KeyScope.Global, "View",
            () => { if (Vm is not null) Vm.ActivePanel = SidePanel.Schema; }));
        r.Register(KeyCommand.Sync(CommandIds.PanelScripts, "Show Scripts panel", KeyScope.Global, "View",
            () => { if (Vm is not null) Vm.ActivePanel = SidePanel.Scripts; }));
        r.Register(KeyCommand.Sync(CommandIds.PanelHistory, "Show History panel", KeyScope.Global, "View",
            () => { if (Vm is not null) Vm.ActivePanel = SidePanel.History; }));
        r.Register(new KeyCommand(CommandIds.ConnectionNew, "New connection…", KeyScope.Global, "Connection", async () => await AddConnectionAsync()));
        r.Register(new KeyCommand(CommandIds.QueryRunAll, "Run entire script", KeyScope.Global, "Query", async () => await RunAllAsync()));
        r.Register(new KeyCommand(CommandIds.SettingsKeybindings, "Keyboard shortcuts…", KeyScope.Global, "View", async () => await EditKeybindingsAsync()));

        // ---- Editor ----
        r.Register(KeyCommand.Sync(CommandIds.EditorOpenLineBelow, "Open line below", KeyScope.Editor, "Editor", () => OpenLine(below: true)));
        r.Register(KeyCommand.Sync(CommandIds.EditorOpenLineAbove, "Open line above", KeyScope.Editor, "Editor", () => OpenLine(below: false)));
        r.Register(KeyCommand.Sync(CommandIds.EditorToggleComment, "Toggle comment", KeyScope.Editor, "Editor", ToggleLineComment));
        r.Register(KeyCommand.Sync(CommandIds.EditorSelectStatement, "Select statement", KeyScope.Editor, "Editor", SelectCurrentQuery));
        r.Register(KeyCommand.Sync(CommandIds.EditorFoldCurrent, "Fold current", KeyScope.Editor, "Editor", () => _folding.FoldCurrent()));
        r.Register(KeyCommand.Sync(CommandIds.EditorUnfoldCurrent, "Unfold current", KeyScope.Editor, "Editor", () => _folding.UnfoldCurrent()));
        r.Register(KeyCommand.Sync(CommandIds.EditorFoldAll, "Fold all", KeyScope.Editor, "Editor", () => _folding.FoldAll()));
        r.Register(KeyCommand.Sync(CommandIds.EditorUnfoldAll, "Unfold all", KeyScope.Editor, "Editor", () => _folding.UnfoldAll()));

        // Navigation/focus commands are claimed in a window tunnel handler so the framework's own tab
        // traversal and the editor/grid don't swallow them first.
        _navCommands = new System.Collections.Generic.HashSet<string>
        {
            CommandIds.TabNext, CommandIds.TabPrev, CommandIds.TabMruNext, CommandIds.TabMruPrev,
            CommandIds.FocusCycle, CommandIds.FocusEditor, CommandIds.FocusResults,
            CommandIds.SelectProject, CommandIds.SelectConnection, CommandIds.SelectDatabase,
        };
        for (var n = 1; n <= 9; n++) _navCommands.Add(CommandIds.TabGoto(n));
    }

    /// <summary>Insert a blank line below (or above) the caret's line, matching its indentation.</summary>
    private void OpenLine(bool below)
    {
        var doc = Editor.Document;
        var line = doc.GetLineByOffset(Editor.CaretOffset);
        var lineText = doc.GetText(line.Offset, line.Length);
        var indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];

        if (below)
        {
            doc.Insert(line.EndOffset, "\n" + indent);
            Editor.CaretOffset = line.EndOffset + 1 + indent.Length;
        }
        else
        {
            doc.Insert(line.Offset, indent + "\n");
            Editor.CaretOffset = line.Offset + indent.Length;
        }
        Editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>Ctrl+/: toggle <c>-- </c> comments over the lines the caret/selection touches.</summary>
    private void ToggleLineComment()
    {
        var start = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
        var end = Editor.SelectionLength > 0 ? Editor.SelectionStart + Editor.SelectionLength : Editor.CaretOffset;
        var result = Squirrel.Sql.LineCommenter.Toggle(Editor.Text, start, end);
        if (result.Text == Editor.Text) return;

        Editor.Document.Replace(0, Editor.Document.TextLength, result.Text);
        Editor.SelectionStart = result.SelectionStart;
        Editor.SelectionLength = result.SelectionLength;
        Editor.CaretOffset = result.SelectionStart + result.SelectionLength;
    }

    /// <summary>Ctrl+Shift+A: select the whole statement the caret sits in.</summary>
    private void SelectCurrentQuery()
    {
        if (Squirrel.Sql.StatementSplitter.StatementAt(Editor.Text, Editor.CaretOffset) is not { } stmt) return;
        Editor.SelectionStart = stmt.TrimmedStart;
        Editor.SelectionLength = stmt.TrimmedEnd - stmt.TrimmedStart;
        Editor.CaretOffset = stmt.TrimmedEnd;
    }

    // Tracks whether Alt was pressed on its own (no other key during the hold) → a "tap" toggles the menu.
    private bool _altAlone;

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // Releasing Ctrl ends a Ctrl+Tab MRU cycle and commits the landed tab as most-recent.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && _mruCycling)
        {
            _mruCycling = false;
            if (Vm?.SelectedTab is { } t) _tabMru.Use(t);
        }
        if (e.Key is Key.LeftAlt or Key.RightAlt && _altAlone && Vm is not null)
        {
            _altAlone = false;
            Vm.IsMenuVisible = !Vm.IsMenuVisible;
            if (Vm.IsMenuVisible) Dispatcher.UIThread.Post(() => MainMenu.Focus()); // enable keyboard menu nav
        }
    }

    /// <summary>A press anywhere outside the menu bar dismisses it. Clicks on an open submenu land on a
    /// separate popup top-level, so they never reach this window handler — only genuine outside clicks do.</summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm?.IsMenuVisible == true && e.Source is Visual v && !IsWithin(v, MainMenu))
            Vm.IsMenuVisible = false;
    }

    /// <summary>Invoking a leaf menu item (one that does something, not a submenu header) closes the bar.</summary>
    private void OnMenuItemInvoked(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && e.Source is MenuItem { ItemCount: 0 }) Vm.IsMenuVisible = false;
    }

    private static bool IsWithin(Visual? node, Visual root)
    {
        for (; node is not null; node = node.GetVisualParent())
            if (ReferenceEquals(node, root)) return true;
        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // While an overlay (palette / quick-pick) is up it owns the keyboard — don't fire globals under it.
        if (AnyOverlayOpen) return;
        // Alt-tap tracking: a lone Alt press arms the menu toggle (fired on key-up); any other key cancels it.
        _altAlone = e.Key is Key.LeftAlt or Key.RightAlt;
        _dispatcher.TryHandle(e, KeyScope.Global); // Global scope; Editor/Grid scopes are handled in their tunnels
    }

    private async Task RunAsync()
    {
        if (Vm is null) return;
        var selected = Editor.SelectedText;
        var sql = string.IsNullOrWhiteSpace(selected)
            ? Squirrel.Sql.StatementSplitter.StatementAt(Editor.Text, Editor.CaretOffset)?.Text ?? Editor.Text
            : selected;
        // A selection (or whole buffer) may hold several blank-line-separated statements without
        // semicolons — normalize so they run as a batch instead of one malformed command.
        sql = Squirrel.Sql.StatementSplitter.EnsureSeparated(sql);
        await Vm.ExecuteAsync(sql);
        RebuildResults(Vm.SelectedTab);
    }

    /// <summary>query.runAll: run the entire buffer as a batch, ignoring caret/selection.</summary>
    private async Task RunAllAsync()
    {
        if (Vm is null) return;
        await Vm.ExecuteAsync(Squirrel.Sql.StatementSplitter.EnsureSeparated(Editor.Text));
        RebuildResults(Vm.SelectedTab);
    }

    /// <summary>tab.next / tab.prev: move to the adjacent tab in visual (strip) order, wrapping around.</summary>
    private void SelectAdjacentTab(int dir)
    {
        if (Vm is null || Vm.Tabs.Count == 0) return;
        var i = Vm.SelectedTab is { } t ? Vm.Tabs.IndexOf(t) : 0;
        Vm.SelectedTab = Vm.Tabs[(i + dir + Vm.Tabs.Count) % Vm.Tabs.Count];
    }

    /// <summary>tab.mruNext / tab.mruPrev: cycle through tabs in most-recently-used order while Ctrl is
    /// held; releasing Ctrl (see <see cref="OnKeyUp"/>) commits the landed tab as most-recent.</summary>
    private void CycleMru(int dir)
    {
        if (Vm is null) return;
        _tabMru.Sync(Vm.Tabs);
        var items = _tabMru.Items;
        if (items.Count < 2) return;
        if (!_mruCycling) { _mruCycling = true; _mruIndex = 0; }
        _mruIndex = (_mruIndex + dir + items.Count) % items.Count;
        Vm.SelectedTab = items[_mruIndex];
    }

    /// <summary>tab.goto{n}: jump to tab n (1-based); n=9 is "last tab" (browser convention). Clamps.</summary>
    private void SelectTabByIndex(int n)
    {
        if (Vm is null || Vm.Tabs.Count == 0) return;
        var idx = n >= 9 ? Vm.Tabs.Count - 1 : System.Math.Min(n - 1, Vm.Tabs.Count - 1);
        Vm.SelectedTab = Vm.Tabs[idx];
    }

    private void FocusResultsPane()
    {
        if (ResultsView.IsVisible) ResultsView.FocusableGrid?.Focus();
    }

    // select.project / connection / database: open a filterable quick-pick (type to filter, ↑/↓, Enter).
    private void OpenProjectPicker()
    {
        if (Vm is null) return;
        ShowQuickPick("Select project…", Vm.RecentProjects.Select(p =>
            (p.Name, (Action)(() => ProjectCombo.SelectedItem = p))).ToList());
    }

    private void OpenConnectionPicker()
    {
        if (Vm is null) return;
        ShowQuickPick("Select connection…", Vm.Connections.Select(c =>
            (c.Name, (Action)(() => Vm.SelectedTabConnection = c))).ToList());
    }

    private void OpenDatabasePicker()
    {
        if (Vm is null) return;
        ShowQuickPick("Select database…", Vm.TabDatabases.Select(d =>
            (d, (Action)(() => DatabasePicker.SelectedItem = d))).ToList());
    }

    private void OnWindowNavKey(object? sender, KeyEventArgs e)
    {
        if (AnyOverlayOpen) return;                         // an overlay owns the keyboard while open
        _dispatcher.TryHandle(e, KeyScope.Global, _navCommands);
    }

    /// <summary>focus.cycle (F6): move keyboard focus editor → results grid → sidebar → editor,
    /// skipping regions that aren't currently shown.</summary>
    private void CycleFocus()
    {
        // Regions in cycle order: (control to focus, container used to detect "currently here"). Editor is
        // always present; results/sidebar only when shown. Detection uses the whole ResultsView/tree so a
        // focused inner element (a grid cell presenter) still classifies correctly.
        var regions = new System.Collections.Generic.List<(Control Focus, Visual Container)>
        {
            (Editor.TextArea, Editor),
        };
        if (ResultsView.IsVisible && ResultsView.FocusableGrid is { } grid) regions.Add((grid, ResultsView));
        if (SidebarFocusTarget() is { } side) regions.Add((side, side));
        if (regions.Count < 2) { regions[0].Focus.Focus(); return; }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var cur = focused is null ? -1 : regions.FindIndex(r => IsWithin(focused, r.Container));
        var start = cur < 0 ? 0 : cur;
        for (var step = 1; step <= regions.Count; step++)      // move to the next region that can take focus
            if (regions[(start + step) % regions.Count].Focus.Focus())
                return;
    }

    /// <summary>The active side panel's primary control, or null when the sidebar is collapsed.</summary>
    private Control? SidebarFocusTarget()
    {
        if (Vm?.SidePaneOpen != true) return null;
        if (SchemaTree.IsVisible) return SchemaTree;
        if (ScriptsTree.IsVisible) return ScriptsTree;
        return null;
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();

    private async Task OpenAsync()
    {
        if (Vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL script",
            AllowMultiple = false,
            FileTypeFilter = new[] { SqlFileType },
            SuggestedStartLocation = await StartFolder(),
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await Vm.LoadScriptIntoSelectedAsync(path);
            LoadEditorFromSelectedTab();
        }
    }

    private async Task SaveAsync()
    {
        if (Vm is null) return;
        if (Vm.SelectedTab?.ScriptPath is { } existing)
        {
            await Vm.SaveSelectedScriptAsync(existing, Editor.Text);
            return;
        }
        await SaveAsAsync();
    }

    /// <summary>Always prompt for a destination (File ▸ Save As…), even for a file-backed tab.</summary>
    private async Task SaveAsAsync()
    {
        if (Vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save SQL script",
            DefaultExtension = "sql",
            SuggestedFileName = System.IO.Path.GetFileName(Vm.SelectedTab?.ScriptPath ?? "query.sql"),
            FileTypeChoices = new[] { SqlFileType },
            SuggestedStartLocation = await StartFolder(),
        });
        if (file?.TryGetLocalPath() is { } path)
            await Vm.SaveSelectedScriptAsync(path, Editor.Text);
    }

    private async Task<IStorageFolder?> StartFolder()
        => Vm?.ScriptsDirectory is { } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;

    private static readonly FilePickerFileType SqlFileType = new("SQL scripts") { Patterns = new[] { "*.sql" } };

    /// <summary>Flush the live editor text/caret into the selected tab (called before close/save session).</summary>
    internal void FlushActiveEditor()
    {
        if (Vm?.SelectedTab is { } tab)
        {
            tab.Text = Editor.Text;
            tab.CaretOffset = Editor.CaretOffset;
        }
    }

    /// <summary>Render the given tab's current result frame, plus the back-bar state (FK-nav history).</summary>
    internal void RebuildResults(EditorTabViewModel? tab)
    {
        HidePendingScript(); // a new run / tab switch invalidates the pending-changes panel
        ResultsView.CanGoBack = tab?.CanGoBack ?? false;
        ResultsView.Results = tab?.Results; // assignment triggers the rebuild (reads CanGoBack)
        SetResultsVisible(tab?.Results is { Count: > 0 }); // reveal on results, collapse when none
    }

    // ---- Floating pending-changes script panel (design RESULTS_GRID §5) ----------------------
    private Control? _pendingScriptOverlay;

    /// <summary>Open a floating, color-coded panel of the write statements a save would run, over a dim
    /// backdrop (bottom-right). Copy / Discard / Run &amp; save act on the result set's pending changes.</summary>
    private void ShowPendingScript(ResultSetViewModel rs)
    {
        if (Vm is null) return;
        HidePendingScript();
        var statements = Vm.PreviewChangeStatements(rs);
        if (statements.Count == 0) return;
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer) return;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        backdrop.PointerPressed += (_, _) => HidePendingScript(); // click outside closes

        var panel = BuildPendingScriptPanel(rs, statements);
        panel.HorizontalAlignment = HorizontalAlignment.Right;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Margin = new Thickness(0, 0, 20, 20);

        var host = new Grid();
        host.Children.Add(backdrop);
        host.Children.Add(panel);
        _pendingScriptOverlay = host;
        layer.Children.Add(host);
    }

    private void HidePendingScript()
    {
        if (_pendingScriptOverlay is { } o)
        {
            OverlayLayer.GetOverlayLayer(this)?.Children.Remove(o);
            _pendingScriptOverlay = null;
        }
    }

    // ---- command palette (Ctrl+Shift+P) ----
    private Control? _paletteOverlay;
    private TextBox? _paletteSearch;
    private ListBox? _paletteList;

    /// <summary>A Grid sized to the whole window, so an overlay's centered panel actually centers — the
    /// OverlayLayer otherwise arranges children at their desired size, which pins them to the top-left.</summary>
    private Grid FillHost()
    {
        var host = new Grid();
        host[!Layoutable.WidthProperty] = new Binding { Source = this, Path = "Bounds.Width" };
        host[!Layoutable.HeightProperty] = new Binding { Source = this, Path = "Bounds.Height" };
        return host;
    }

    /// <summary>Open the command palette: a fuzzy-searchable list of every applicable command with its
    /// current gesture. Re-invoking while open closes it (toggle). Self-handles its own keys, so global
    /// shortcuts are suppressed while it's up (see <see cref="OnKeyDown"/>).</summary>
    private void ShowPalette()
    {
        if (Vm is null) return;
        if (_paletteOverlay is not null) { HidePalette(); return; }
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer) return;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        backdrop.PointerPressed += (_, _) => HidePalette();

        _paletteSearch = new TextBox { Watermark = "Type a command…" };
        _paletteSearch.TextChanged += (_, _) => RefreshPaletteList();

        _paletteList = new ListBox
        {
            MaxHeight = 380,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<PaletteRow>((row, _) => BuildPaletteRow(row), supportsRecycling: true),
        };
        _paletteList.DoubleTapped += (_, _) => RunSelectedPaletteCommand();

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_paletteSearch, Dock.Top);
        _paletteSearch.Margin = new Thickness(0, 0, 0, 6);
        content.Children.Add(_paletteSearch);
        content.Children.Add(_paletteList);

        var panel = new Border
        {
            Width = 560,
            Padding = new Thickness(10),
            Background = ThemeBrush("Bg.Chrome"),
            BorderBrush = ThemeBrush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 120, 0, 0),
            Child = content,
        };

        var host = FillHost();
        host.Children.Add(backdrop);
        host.Children.Add(panel);
        host.AddHandler(KeyDownEvent, OnPaletteKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _paletteOverlay = host;
        layer.Children.Add(host);

        RefreshPaletteList();
        _paletteSearch.Focus();
    }

    private void HidePalette()
    {
        if (_paletteOverlay is { } o)
        {
            OverlayLayer.GetOverlayLayer(this)?.Children.Remove(o);
            _paletteOverlay = null;
            _paletteSearch = null;
            _paletteList = null;
        }
    }

    // ---- generic filterable quick-pick (project / connection / database) ----
    private bool AnyOverlayOpen => _paletteOverlay is not null || _quickPickOverlay is not null;
    private Control? _quickPickOverlay;
    private TextBox? _quickPickSearch;
    private ListBox? _quickPickList;
    private System.Collections.Generic.IReadOnlyList<(string Label, Action Pick)> _quickPickItems = System.Array.Empty<(string, Action)>();

    private sealed record QuickPickRow(string Label, Action Pick);

    /// <summary>A single filterable list overlay (type to filter, ↑/↓, Enter). Opening one replaces any
    /// other, so only one picker is ever active.</summary>
    private void ShowQuickPick(string placeholder, System.Collections.Generic.IReadOnlyList<(string Label, Action Pick)> items)
    {
        if (Vm is null || items.Count == 0) return;
        HidePalette();
        HideQuickPick();
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer) return;
        _quickPickItems = items;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        backdrop.PointerPressed += (_, _) => HideQuickPick();

        _quickPickSearch = new TextBox { Watermark = placeholder };
        _quickPickSearch.TextChanged += (_, _) => RefreshQuickPick();
        _quickPickList = new ListBox
        {
            MaxHeight = 380,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<QuickPickRow>((row, _) =>
                new TextBlock { Text = row.Label, Margin = new Thickness(4, 2), Foreground = ThemeBrush("Text.Primary") }, supportsRecycling: true),
        };
        _quickPickList.DoubleTapped += (_, _) => RunSelectedQuickPick();

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_quickPickSearch, Dock.Top);
        _quickPickSearch.Margin = new Thickness(0, 0, 0, 6);
        content.Children.Add(_quickPickSearch);
        content.Children.Add(_quickPickList);

        var panel = new Border
        {
            Width = 460,
            Padding = new Thickness(10),
            Background = ThemeBrush("Bg.Chrome"),
            BorderBrush = ThemeBrush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 120, 0, 0),
            Child = content,
        };

        var host = FillHost();
        host.Children.Add(backdrop);
        host.Children.Add(panel);
        host.AddHandler(KeyDownEvent, OnQuickPickKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _quickPickOverlay = host;
        layer.Children.Add(host);

        RefreshQuickPick();
        _quickPickSearch.Focus();
    }

    private void HideQuickPick()
    {
        if (_quickPickOverlay is { } o)
        {
            OverlayLayer.GetOverlayLayer(this)?.Children.Remove(o);
            _quickPickOverlay = null;
            _quickPickSearch = null;
            _quickPickList = null;
        }
    }

    private void RefreshQuickPick()
    {
        if (_quickPickList is null) return;
        var query = _quickPickSearch?.Text ?? "";
        System.Collections.Generic.IEnumerable<(string Label, Action Pick)> filtered = string.IsNullOrWhiteSpace(query)
            ? _quickPickItems
            : _quickPickItems
                .Select(x => (x, score: PaletteFilter.Score(x.Label, query.Trim())))
                .Where(t => t.score.HasValue)
                .OrderByDescending(t => t.score!.Value)
                .Select(t => t.x);
        _quickPickList.ItemsSource = filtered.Select(x => new QuickPickRow(x.Label, x.Pick)).ToList();
        if (_quickPickList.ItemCount > 0) _quickPickList.SelectedIndex = 0;
    }

    private void MoveQuickPickSelection(int dir)
    {
        if (_quickPickList is null || _quickPickList.ItemCount == 0) return;
        var n = _quickPickList.ItemCount;
        _quickPickList.SelectedIndex = (_quickPickList.SelectedIndex + dir + n) % n;
        _quickPickList.ScrollIntoView(_quickPickList.SelectedIndex);
    }

    private void RunSelectedQuickPick()
    {
        if (_quickPickList?.SelectedItem is not QuickPickRow row) return;
        HideQuickPick();
        row.Pick();
    }

    private void OnQuickPickKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: HideQuickPick(); e.Handled = true; break;
            case Key.Enter: RunSelectedQuickPick(); e.Handled = true; break;
            case Key.Down: MoveQuickPickSelection(+1); e.Handled = true; break;
            case Key.Up: MoveQuickPickSelection(-1); e.Handled = true; break;
        }
    }

    private void RefreshPaletteList()
    {
        if (_paletteList is null) return;
        var query = _paletteSearch?.Text ?? "";
        var rows = PaletteFilter.Rank(_commands.All.Where(c => c.CanRun()), query)
            .Select(c => new PaletteRow(c, _dispatcher.Keymap.DisplayGesture(c.Id)))
            .ToList();
        _paletteList.ItemsSource = rows;
        if (rows.Count > 0) _paletteList.SelectedIndex = 0;
    }

    private void MovePaletteSelection(int dir)
    {
        if (_paletteList is null || _paletteList.ItemCount == 0) return;
        var n = _paletteList.ItemCount;
        _paletteList.SelectedIndex = (_paletteList.SelectedIndex + dir + n) % n;
        _paletteList.ScrollIntoView(_paletteList.SelectedIndex);
    }

    private void RunSelectedPaletteCommand()
    {
        if (_paletteList?.SelectedItem is not PaletteRow row) return;
        HidePalette();
        if (row.Command.CanRun()) CrashReporter.Observe(row.Command.Run(), $"command '{row.Command.Id}'");
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: HidePalette(); e.Handled = true; break;
            case Key.Enter: RunSelectedPaletteCommand(); e.Handled = true; break;
            case Key.Down: MovePaletteSelection(+1); e.Handled = true; break;
            case Key.Up: MovePaletteSelection(-1); e.Handled = true; break;
        }
    }

    /// <summary>A palette row: the command plus its current gesture text (may be null when unbound).</summary>
    private sealed record PaletteRow(KeyCommand Command, string? Gesture);

    private Control BuildPaletteRow(PaletteRow row)
    {
        var title = new TextBlock { Text = row.Command.Title, VerticalAlignment = VerticalAlignment.Center };
        var group = new TextBlock
        {
            Text = row.Command.Group,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("Text.Faint"),
            FontSize = 11,
        };
        var gesture = new TextBlock
        {
            Text = row.Gesture ?? "",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("Text.Dim"),
            FontSize = 11,
        };
        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(gesture, Dock.Right);
        DockPanel.SetDock(title, Dock.Left);
        DockPanel.SetDock(group, Dock.Left);
        dock.Children.Add(gesture);
        dock.Children.Add(title);
        dock.Children.Add(group);
        return dock;
    }

    private Control BuildPendingScriptPanel(ViewModels.ResultSetViewModel rs, System.Collections.Generic.IReadOnlyList<MainWindowViewModel.PendingStatement> statements)
    {
        // Header: "N statements" + copy.
        var count = new TextBlock
        {
            Text = statements.Count == 1 ? "1 statement" : $"{statements.Count} statements",
            Foreground = ThemeBrush("Text.Dim"), FontSize = 11, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new Button { Content = "⧉ Copy", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ThemeBrush("Text.Dim"), Padding = new Thickness(6, 2) };
        copy.Click += (_, _) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(string.Join("\n", statements.Select(s => s.Sql)));
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(copy, 1);
        headerGrid.Children.Add(count);
        headerGrid.Children.Add(copy);
        var header = new Border { Padding = new Thickness(12, 8), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeBrush("Border"), Child = headerGrid };
        DockPanel.SetDock(header, Dock.Top);

        // Body: line-numbered, kind-colored statements.
        var list = new StackPanel { Spacing = 2 };
        for (var i = 0; i < statements.Count; i++)
        {
            var num = new TextBlock { Text = $"{i + 1,3} ", Foreground = ThemeBrush("Text.Faint"), FontFamily = MonoFont, VerticalAlignment = VerticalAlignment.Top };
            var sql = new TextBlock { Text = statements[i].Sql, Foreground = KindBrush(statements[i].Kind), FontFamily = MonoFont, TextWrapping = TextWrapping.Wrap };
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
            rowPanel.Children.Add(num);
            rowPanel.Children.Add(sql);
            list.Children.Add(rowPanel);
        }
        var body = new ScrollViewer { Content = new Border { Padding = new Thickness(12, 8), Child = list } };

        // Footer: Discard + Run & save.
        var discard = new Button { Content = "Discard", Margin = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent, BorderBrush = ThemeBrush("Error.Red"), BorderThickness = new Thickness(1), Foreground = ThemeBrush("Error.Red") };
        discard.Click += async (_, _) => { HidePendingScript(); if (Vm is not null) { await Vm.DiscardChangesAsync(rs); RebuildResults(Vm.SelectedTab); } };
        var run = new Button { Content = "✓ Run & save", Background = ThemeBrush("Ok.Green"), Foreground = ThemeBrush("Bg.Editor") };
        run.Click += async (_, _) => { HidePendingScript(); if (Vm is not null) { await Vm.SaveChangesAsync(rs); ResultsView.RefreshRowHighlights(); } };
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footerButtons.Children.Add(discard);
        footerButtons.Children.Add(run);
        var footer = new Border { Padding = new Thickness(12, 8), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeBrush("Border"), Child = footerButtons };
        DockPanel.SetDock(footer, Dock.Bottom);

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(body);

        return new Border
        {
            Width = 520,
            MaxHeight = 420,
            Background = ThemeBrush("Bg.Chrome"),
            BorderBrush = ThemeBrush("Border.Control"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 20, Blur = 50, Spread = -12, Color = Color.FromArgb(0xBF, 0, 0, 0) }),
            Child = dock,
        };
    }

    private static readonly FontFamily MonoFont = new("Iosevka Nerd Font Mono,Cascadia Code,Consolas,Menlo,monospace");

    /// <summary>Statement color by kind: INSERT green, UPDATE amber, DELETE red (design §5).</summary>
    private IBrush KindBrush(string kind) => kind switch
    {
        "INSERT" => ThemeBrush("Ok.Green"),
        "UPDATE" => new SolidColorBrush(Color.FromRgb(0xE6, 0xC3, 0x84)),
        "DELETE" => ThemeBrush("Error.Red"),
        _ => ThemeBrush("Text.Primary"),
    };

    /// <summary>Show SQL in a read-only, monospace preview window (selectable to copy).</summary>
    private void ShowSqlPreview(string sql, string title = "SQL preview — changes to save")
    {
        var box = new AvaloniaEdit.TextEditor
        {
            Text = sql,
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Code,Cascadia Mono,Consolas,Menlo,monospace"),
            FontSize = 13,
            Margin = new Thickness(8),
            ShowLineNumbers = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var win = new Window
        {
            Title = title,
            Width = 720,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var close = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(8, 0, 8, 8) };
        close.Click += (_, _) => win.Close();
        DockPanel.SetDock(close, Dock.Bottom);

        var panel = new DockPanel();
        panel.Children.Add(close);
        panel.Children.Add(box);
        win.Content = panel;
        win.Show(this);
    }
}
