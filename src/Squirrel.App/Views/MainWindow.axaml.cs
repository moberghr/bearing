using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit.TextMate;
using Squirrel.App.Completion;
using Squirrel.App.Editing;
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
    private bool _loadingEditor;          // guards editor<->tab sync while swapping tabs
    private bool _suppressProjectChange;   // guards the project combo during programmatic updates

    public MainWindow()
    {
        InitializeComponent();
        InstallSqlHighlighting();

        _completion = new CompletionController(Editor, new CompletionEngine(), () => Vm?.SnapshotForSelectedTab());
        _folding = new SqlFoldingController(Editor); // installs the fold margin (left of the text)
        Editor.TextArea.LeftMargins.Add(_statementHighlight); // its own column, right of the line numbers

        // Editor-editing shortcuts must pre-empt AvaloniaEdit, which consumes Enter/'/'/brackets on
        // its own KeyDown — so handle them during the tunnel phase, before the editor sees them.
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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
        ResultsView.PreviewSql = rs =>
        {
            if (Vm?.PreviewChanges(rs) is { } sql) ShowSqlPreview(sql);
        };

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
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void HookViewModel()
    {
        if (Vm is null) return;
        Vm.PropertyChanged -= OnViewModelPropertyChanged;
        Vm.PropertyChanged += OnViewModelPropertyChanged;
        LoadEditorFromSelectedTab();
        SyncProjectCombo();
        App.SetConnectionAccent(Vm.ActiveConnectionColor); // seed the accent for the initial tab
    }

    private void InstallSqlHighlighting() => SetupEditorChrome(Editor);

    private void SetupEditorChrome(AvaloniaEdit.TextEditor editor)
    {
        // DarkPlus supplies the grammar token colors. Exact Kanagawa syntax hues are deferred (a
        // custom TextMate theme needs internal TextMateSharp APIs; the handoff flags syntax colors
        // as its one deliberately-loose area — docs/design/editor-4a/README.md §Fidelity).
        var options = new RegistryOptions(ThemeName.DarkPlus);
        var installation = editor.InstallTextMate(options);
        var sql = options.GetLanguageByExtension(".sql");
        if (sql is not null)
            installation.SetGrammar(options.GetScopeByLanguageId(sql.Id));

        // Editor chrome the TextMate theme doesn't drive to spec: Kanagawa surface (#1F1F28),
        // current-line highlight (#252535), and faint line numbers (#54546D).
        editor.Background = ThemeBrush("Bg.Editor");
        editor.LineNumbersForeground = ThemeBrush("Text.Faint");
        editor.Options.HighlightCurrentLine = true;
        var lineActive = ((SolidColorBrush)ThemeBrush("Bg.LineActive")).Color;
        editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(lineActive);
        editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(lineActive)); // no contrasting box
    }

    /// <summary>Resolve a token brush from app resources (falls back to transparent if missing).</summary>
    private IBrush ThemeBrush(string key)
        => (Application.Current?.FindResource(key) as IBrush) ?? Brushes.Transparent;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
            LoadEditorFromSelectedTab();
        else if (e.PropertyName == nameof(MainWindowViewModel.ActiveConnectionColor))
            App.SetConnectionAccent(Vm?.ActiveConnectionColor); // recolor tab accent, dots, results, status line
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

    private async void OnAddConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new ConnectionDialog(null, null, (i, p, ct) => Vm.TestConnectionAsync(i, p, ct));
        var result = await dialog.ShowDialog<ConnectionDialogResult?>(this);
        if (result is { Delete: false }) await Vm.AddOrUpdateConnectionAsync(result.Connection, result.Password);
    }

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
        ApplyTreeSearch(advance: true);
    }

    private void OnSchemaTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _treeSearch.Length > 0) { ClearTreeSearch(); e.Handled = true; }
        else if (e.Key == Key.Back && _treeSearch.Length > 0)
        {
            _treeSearch = _treeSearch[..^1];
            e.Handled = true;
            if (_treeSearch.Length == 0) ClearTreeSearch(); else ApplyTreeSearch(advance: false);
        }
    }

    private void ClearTreeSearch()
    {
        _treeSearch = "";
        foreach (var n in FlattenRealized()) n.IsMatch = false;
        if (Vm is not null) Vm.StatusText = "";
    }

    private void ApplyTreeSearch(bool advance)
    {
        var nodes = FlattenRealized();
        var matches = nodes.Where(n => FuzzyMatch(n.Title, _treeSearch)).ToList();
        foreach (var n in nodes) n.IsMatch = false;
        foreach (var m in matches) m.IsMatch = true;

        if (matches.Count == 0) { Vm!.StatusText = $"No match for “{_treeSearch}”."; return; }

        // Select the first match at/after the current selection (advance moves past it) so typing cycles.
        var current = SchemaTree.SelectedItem as SchemaNodeViewModel;
        var startIdx = current is null ? -1 : nodes.IndexOf(current);
        var next = matches.FirstOrDefault(m => nodes.IndexOf(m) > startIdx) ?? matches[0];
        if (!advance && current is not null && FuzzyMatch(current.Title, _treeSearch)) next = current;

        SchemaTree.SelectedItem = next;
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
        var dialog = new ConnectionDialog(existing, password, (i, p, ct) => Vm.TestConnectionAsync(i, p, ct));
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

    // ---- menu bar (Alt) + focus mode ----

    /// <summary>Esc unwinds, most-modal first: the menu bar → a running query.</summary>
    private bool HandleEscape()
    {
        if (Vm is null) return false;
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
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.StatusText = "Squirrel — SQL query editor.";
    }

    /// <summary>
    /// Editor-scoped editing shortcuts, handled in the tunnel phase so they win over AvaloniaEdit's
    /// own handling of Enter / '/' / brackets. App-level shortcuts (Run, Save, …) stay in <see cref="OnKeyDown"/>.
    /// </summary>
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (alt) return;

        // Fold / unfold the current query. Match the physical [ ] keys so it works on any layout —
        // on the Croatian keyboard those same physical keys type š / đ.
        if (ctrl && shift && e.PhysicalKey is PhysicalKey.BracketLeft or PhysicalKey.BracketRight)
        {
            if (e.PhysicalKey == PhysicalKey.BracketLeft) _folding.FoldCurrent();
            else _folding.UnfoldCurrent();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when shift && !ctrl: OpenLine(below: true); break;
            case Key.Enter when shift && ctrl: OpenLine(below: false); break;
            // Ctrl+/ on US, Ctrl+- on the HR layout (that physical key reports as OemMinus there).
            case Key.OemQuestion when ctrl && !shift: ToggleLineComment(); break;
            case Key.OemMinus when ctrl && !shift: ToggleLineComment(); break;
            case Key.A when ctrl && shift: SelectCurrentQuery(); break;
            case Key.OemMinus when ctrl && shift: _folding.FoldAll(); break;
            case Key.OemPlus when ctrl && shift: _folding.UnfoldAll(); break;
            default: return;
        }
        e.Handled = true;
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
        if (e.Key is Key.LeftAlt or Key.RightAlt && _altAlone && Vm is not null)
        {
            _altAlone = false;
            Vm.IsMenuVisible = !Vm.IsMenuVisible;
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Alt-tap tracking: a lone Alt press arms the toggle; any other key cancels it.
        _altAlone = e.Key is Key.LeftAlt or Key.RightAlt;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (e.Key == Key.F5 || (e.Key == Key.Enter && ctrl)) { e.Handled = true; await RunAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = HandleEscape(); }
        else if (e.Key == Key.Up && alt) { e.Handled = true; MoveToAdjacentStatement(-1); }
        else if (e.Key == Key.Down && alt) { e.Handled = true; MoveToAdjacentStatement(+1); }
        else if (e.Key == Key.Space && ctrl) { e.Handled = true; _completion.TriggerExplicit(); }
        else if (e.Key == Key.S && ctrl) { e.Handled = true; await SaveAsync(); }
        else if (e.Key == Key.O && ctrl) { e.Handled = true; await OpenAsync(); }
        else if (e.Key == Key.T && ctrl) { e.Handled = true; Vm?.NewTab(); }
        else if (e.Key == Key.B && ctrl) { e.Handled = true; if (Vm is not null) Vm.SidePaneOpen = !Vm.SidePaneOpen; }
        else if (e.Key == Key.W && ctrl && Vm?.SelectedTab is { } tab) { e.Handled = true; Vm.CloseTab(tab); }
        else if (e.Key == Key.F2 && Vm?.SelectedTab is { } rt) { e.Handled = true; await RenameTabAsync(rt); }
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
        ResultsView.CanGoBack = tab?.CanGoBack ?? false;
        ResultsView.Results = tab?.Results; // assignment triggers the rebuild (reads CanGoBack)
    }

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
