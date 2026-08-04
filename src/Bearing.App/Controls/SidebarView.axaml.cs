using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.App.Views;

namespace Bearing.App.Controls;

/// <summary>
/// The left side panel: the swappable Connections / Scripts / History views plus the pane resize grip.
/// Its DataContext is the shell <see cref="ShellViewModel"/> (inherited from the window), so every
/// binding resolves against the same view-model the shell uses. Self-contained interactions (tree fuzzy
/// jump, scripts drag &amp; drop, connection add/edit/delete dialogs, history preview) live here; the three
/// callbacks below hand back the actions the shell still owns (the editor, the SQL-preview overlay).
/// </summary>
public partial class SidebarView : UserControl
{
    /// <summary>The ＋ button asks the shell to open the new-connection dialog (also driven by the palette).</summary>
    public System.Action? AddConnectionRequested { get; set; }

    /// <summary>Raised after an action changed the selected tab, so the shell re-syncs the editor buffer.</summary>
    public System.Action? EditorSyncRequested { get; set; }

    /// <summary>Show a read-only SQL preview (object definition) in the shell's overlay: (sql, title).</summary>
    public System.Action<string, string>? SqlPreviewRequested { get; set; }

    public SidebarView()
    {
        InitializeComponent();
        // Intercept Up/Down/Esc/Backspace before the TreeView's built-in node navigation, so a search
        // cycles matches instead of walking every row (same trick the shell used before extraction).
        SchemaTree.AddHandler(KeyDownEvent, OnSchemaTreeKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private ShellViewModel? Vm => DataContext as ShellViewModel;
    private readonly IDialogService _dialogs = new DialogService(); // owns dialog construction (phase 5)

    /// <summary>The active panel's primary control for F6 focus cycling, or null when collapsed.</summary>
    public Control? FocusTarget
    {
        get
        {
            if (Vm?.SidePaneOpen != true) return null;
            if (SchemaTree.IsVisible) return SchemaTree;
            if (ScriptsTree.IsVisible) return ScriptsTree;
            return null;
        }
    }

    // The history preview row (row 2 of HistoryGrid) grows to show the selected query and collapses to 0
    // when nothing is selected. Subscribing follows the VM so it re-hooks if the shell swaps view-models.
    private ShellViewModel? _hooked;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_hooked is not null) _hooked.History.PropertyChanged -= OnHistoryPropertyChanged;
        _hooked = Vm;
        if (_hooked is not null) _hooked.History.PropertyChanged += OnHistoryPropertyChanged;
        UpdateHistoryPreviewRow();
    }

    private void OnHistoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryPanelViewModel.SelectedRow)) UpdateHistoryPreviewRow();
    }

    // ---- connections ----

    private void OnAddConnectionClick(object? sender, RoutedEventArgs e) => AddConnectionRequested?.Invoke();

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
        if (Vm is not null) Walk(Vm.Connections.ServerNodes);
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
        var password = await Vm.Connections.GetConnectionPasswordAsync(existing.Id);
        var result = await _dialogs.ShowConnectionDialogAsync(existing, password, (i, p, ct) => Vm.Connections.TestConnectionAsync(i, p, ct), Vm.SecretStorageSecure);
        if (result is null) return;
        if (result.Delete) await Vm.Connections.DeleteConnectionAsync(existing.Id);
        else await Vm.Connections.AddOrUpdateConnectionAsync(result.Connection, result.Password);
    }

    private void OnUseConnectionInTab(object? sender, RoutedEventArgs e) => AssignConnectionToTab(NodeOf(sender));

    private void OnSchemaNodeDoubleTapped(object? sender, TappedEventArgs e) => AssignConnectionToTab(NodeOf(sender));

    private void AssignConnectionToTab(SchemaNodeViewModel? node)
    {
        if (Vm?.Workspace.SelectedTab is { } tab && node is ServerNodeViewModel server)
            Vm.Connections.SetTabConnection(tab, server.Connection.Id);
    }

    private async void OnDeleteServer(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && NodeOf(sender) is ServerNodeViewModel server)
            await Vm.Connections.DeleteConnectionAsync(server.Connection.Id);
    }

    private async void OnRefreshServer(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && NodeOf(sender) is ServerNodeViewModel server)
            await Vm.Connections.RefreshServerMetadataAsync(server.Connection.Id);
    }

    private async void OnShowDefinition(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { CanShowDefinition: true } node) return;
        try
        {
            var definition = await node.LoadDefinitionAsync(CancellationToken.None);
            SqlPreviewRequested?.Invoke(string.IsNullOrWhiteSpace(definition) ? "-- (no definition)" : definition, node.DefinitionTitle);
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

    // Scripts panel ＋ button: a fresh scratch tab. The editor re-syncs reactively off the SelectedTab change.
    private void OnNewTabClick(object? sender, RoutedEventArgs e) => Vm?.Workspace.NewTab();

    private static ScriptItem? ScriptOf(object? sender) => (sender as Control)?.DataContext as ScriptItem;

    private async void OnScriptActivated(object? sender, TappedEventArgs e) => await OpenScript(ScriptOf(sender));
    private async void OnOpenScriptClick(object? sender, RoutedEventArgs e) => await OpenScript(ScriptOf(sender));

    private async Task OpenScript(ScriptItem? script)
    {
        if (Vm is not null && script is not null)
        {
            await Vm.Workspace.OpenScriptInNewTabAsync(script.FullPath);
            EditorSyncRequested?.Invoke();
        }
    }

    private async void OnRenameScriptClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || ScriptOf(sender) is not { } script) return;
        var name = await _dialogs.ShowTextPromptAsync("Rename script file", script.Name);
        if (name is not null) await Vm.Scripts.RenameScriptAsync(script.FullPath, name);
    }

    private async void OnNewScriptFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var name = await _dialogs.ShowTextPromptAsync("New folder name", "");
        if (!string.IsNullOrWhiteSpace(name)) Vm.Scripts.CreateScriptFolder(name);
    }

    private static ScriptFolderViewModel? FolderOf(object? sender) => (sender as Control)?.DataContext as ScriptFolderViewModel;

    private async void OnNewSubfolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || FolderOf(sender) is not { } folder) return;
        var name = await _dialogs.ShowTextPromptAsync("New subfolder name", "");
        if (!string.IsNullOrWhiteSpace(name)) Vm.Scripts.CreateScriptFolder(name, folder.FullPath);
    }

    private async void OnNewScriptInFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || FolderOf(sender) is not { } folder) return;
        var name = await _dialogs.ShowTextPromptAsync("New script name", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await Vm.Scripts.CreateScriptFileAsync(folder.FullPath, name) is { } path)
        {
            await Vm.Workspace.OpenScriptInNewTabAsync(path);
            EditorSyncRequested?.Invoke();
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
        DataFormat.CreateInProcessFormat<string>("bearing.script-path");
    private Point _dragStart;
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
            Vm.Scripts.MoveScript(src, folder.FullPath);
            e.Handled = true;
        }
    }

    private void OnScriptDropOnRoot(object? sender, DragEventArgs e)
    {
        if (Vm?.ScriptsDirectory is { } root && e.DataTransfer.TryGetValue(ScriptPathFormat) is string src)
        {
            Vm.Scripts.MoveScript(src, root);
            e.Handled = true;
        }
    }

    // ---- history ----

    private async void OnHistorySearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is not null) { e.Handled = true; await Vm.History.ReloadAsync(CancellationToken.None); }
    }

    // Double-click a history row → open its SQL in a new tab (non-destructive; inherits the connection).
    private void OnHistoryRowActivated(object? sender, TappedEventArgs e)
    {
        if (Vm is not null && (sender as Control)?.DataContext is HistoryRowViewModel row && row.Sql.Length > 0)
        {
            Vm.Workspace.NewTab(row.Sql);
            EditorSyncRequested?.Invoke();
        }
    }

    // ---- history preview row (real pixel row so the splitter resizes it; 0 when nothing selected) ----
    private double _historyPreviewHeight = 220;

    private void UpdateHistoryPreviewRow()
    {
        var row = HistoryGrid.RowDefinitions[2];
        if (Vm?.History.SelectedRow is not null)
        {
            row.Height = new GridLength(_historyPreviewHeight);
        }
        else
        {
            if (row.Height.IsAbsolute && row.Height.Value > 0)
                _historyPreviewHeight = row.Height.Value; // remember the user's drag size
            row.Height = new GridLength(0);
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
}
