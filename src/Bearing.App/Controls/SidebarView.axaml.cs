using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.Core.Data;

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

    /// <summary>Type letters to fuzzy-search the loaded tree: highlight every match and jump the selection to
    /// the next one; repeating the same/extending text cycles through matches. A match under a collapsed
    /// parent is reached by expanding it, not by being skipped.</summary>
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
        var matches = SchemaTreeSearch.Matches(Roots, _treeSearch);
        if (matches.Count == 0) return;
        var current = SchemaTree.SelectedItem as SchemaNodeViewModel;
        var idx = current is null ? -1 : matches.IndexOf(current);
        idx = idx < 0
            ? (direction > 0 ? 0 : matches.Count - 1)
            : (idx + direction + matches.Count) % matches.Count;
        SelectMatch(matches[idx]);
    }

    /// <summary>
    /// Select a match and take keyboard focus with it. Setting <c>SelectedItem</c> alone leaves focus on
    /// whatever row had it — the first row, after F6 into the tree — so Right and Enter expanded *that* row
    /// instead of the highlighted match. The container only exists once the tree has auto-scrolled the
    /// selection into view, which is why the focus call waits for the layout pass.
    /// </summary>
    private void SelectMatch(SchemaNodeViewModel node)
    {
        ExpandAncestorsOf(node);
        SchemaTree.SelectedItem = node;
        Dispatcher.UIThread.Post(() =>
        {
            // A later keystroke may already have moved on; don't yank focus back to a stale match.
            if (!ReferenceEquals(SchemaTree.SelectedItem, node)) return;
            SchemaTree.GetVisualDescendants().OfType<TreeViewItem>()
                .FirstOrDefault(i => ReferenceEquals(i.DataContext, node))
                ?.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Open whatever stands between the tree roots and a match — a Views / Functions bucket, or a table
    /// whose columns are still loaded from an earlier expand — so the selected node is on screen.</summary>
    private void ExpandAncestorsOf(SchemaNodeViewModel node)
    {
        foreach (var ancestor in SchemaTreeSearch.AncestorsOf(Roots, node)) ancestor.IsExpanded = true;
    }

    private System.Collections.Generic.IEnumerable<SchemaNodeViewModel> Roots
        => Vm?.Connections.ServerNodes ?? System.Linq.Enumerable.Empty<SchemaNodeViewModel>();

    /// <summary>The live query as a delegate a node can hold: it reads <c>_treeSearch</c> at call time, so one
    /// assignment stays correct as the text grows, shrinks, and empties (empty matches nothing).</summary>
    private bool MatchesCurrentSearch(string title) => SchemaTreeSearch.FuzzyMatch(title, _treeSearch);

    private void ClearTreeSearch()
    {
        _treeSearch = "";
        foreach (var n in SchemaTreeSearch.Flatten(Roots)) n.IsMatch = false;
        if (Vm is not null) Vm.StatusText = "";
    }

    private void ApplyTreeSearch()
    {
        // Highlight and reset over the *same* set the matching used, collapsed nodes included: a hidden node
        // left out of the reset keeps a previous search's highlight until it is reopened.
        var nodes = SchemaTreeSearch.Flatten(Roots);
        var matches = nodes.Where(n => SchemaTreeSearch.FuzzyMatch(n.Title, _treeSearch)).ToList();
        foreach (var n in nodes)
        {
            n.IsMatch = false;
            // Leave the live query behind on every node: children loaded later inherit it and highlight
            // themselves, so a collapsed table's columns come back tinted instead of blank.
            n.MatchTest = MatchesCurrentSearch;
        }
        foreach (var m in matches) m.IsMatch = true;

        if (matches.Count == 0) { Vm!.StatusText = $"No match for “{_treeSearch}”."; return; }

        // Stay put while the current selection still matches (refining the query shouldn't jump you
        // around); otherwise land on the first match. Down/Up navigate between matches manually.
        var current = SchemaTree.SelectedItem as SchemaNodeViewModel;
        if (current is null || !SchemaTreeSearch.FuzzyMatch(current.Title, _treeSearch))
            SelectMatch(matches[0]);
        Vm!.StatusText = $"“{_treeSearch}” · {matches.Count} match{(matches.Count == 1 ? "" : "es")}";
    }

    private async void OnEditServer(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || NodeOf(sender) is not ServerNodeViewModel server) return;
        var existing = server.Connection;
        var password = await Vm.Connections.GetConnectionPasswordAsync(existing.Id);
        // Re-check for a keychain first: the dialog's warnings and its default credential kind are decided by
        // the posture passed in, and a startup probe that ran too early must not pin them for the session.
        await Vm.RefreshSecretStorageAsync();
        var result = await _dialogs.ShowConnectionDialogAsync(existing, password, (i, p, ct) => Vm.Connections.TestConnectionAsync(i, p, ct), Vm.SecretStorage);
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
            // Definition reads go through SchemaBrowser's own connections, so a connect-time failure can
            // quote a connection string into the status bar — redact it (§1.1).
            if (Vm is not null) Vm.StatusText = $"Could not load definition: {SafeErrorText.Of(ex)}";
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

    private void OnRenameScriptClick(object? sender, RoutedEventArgs e)
    {
        if (ScriptOf(sender) is { } script) BeginScriptRename(script);
    }

    /// <summary>
    /// Turn a script row into an editable box and put the caret in it. The box is already in the template
    /// (hidden), so it can only be focused once a layout pass has made it visible — hence the posted lookup.
    /// </summary>
    private void BeginScriptRename(ScriptItem script)
    {
        script.BeginRename();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var box = ScriptsTree.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(b => ReferenceEquals(b.DataContext, script));
            if (box is null) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private async void OnScriptRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ScriptItem script }) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;   // also keeps the tree's own Enter (open the script) out of it
                await CommitScriptRenameAsync(script);
                break;
            case Key.Escape:
                e.Handled = true;
                script.IsRenaming = false;
                break;
        }
    }

    /// <summary>Clicking away commits, as an inline edit should. Guarded on <c>IsRenaming</c>, which the commit
    /// clears before it awaits, so the focus loss that follows can't come back round for a second rename.</summary>
    private async void OnScriptRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ScriptItem script } && script.IsRenaming)
            await CommitScriptRenameAsync(script);
    }

    private async Task CommitScriptRenameAsync(ScriptItem script)
    {
        var name = script.RenameDraft.Trim();
        script.IsRenaming = false;
        if (Vm is null || name.Length == 0) return;
        await Vm.Scripts.RenameScriptAsync(script.FullPath, name);
    }

    private async void OnNewScriptFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var name = await _dialogs.ShowTextPromptAsync("New folder name", "");
        if (!string.IsNullOrWhiteSpace(name)) Vm.Scripts.CreateScriptFolder(name);
    }

    /// <summary>Delete a script from the tree, behind the same confirm the tab menu uses — and through the
    /// same workspace call, so a tab showing the file closes with it instead of pointing at nothing.</summary>
    private async void OnDeleteScriptClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || (sender as Control)?.DataContext is not ScriptItem script) return;
        if (!await _dialogs.ConfirmDeleteScriptAsync(script.Name)) return;
        if (Vm.Workspace.DeleteScript(script.FullPath)) EditorSyncRequested?.Invoke();
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
    private DragGhost? _ghost;                   // the labelled box that follows the pointer while dragging

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

    private async void OnScriptPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || _dragPress is null || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(null);
        if (System.Math.Abs(pos.X - _dragStart.X) <= 4 && System.Math.Abs(pos.Y - _dragStart.Y) <= 4) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(ScriptPathFormat, _dragItem.FullPath));
        var press = _dragPress;
        var dragged = _dragItem;
        _dragItem = null;
        _dragPress = null;

        // Say what is in flight, two ways that don't depend on the platform: the row fades, and the status
        // bar names the file and the rule for dropping outside a folder. An app-set drag cursor is not one of
        // the options here — the pointer is grabbed for the duration of the drag, so the cursor is the
        // platform's, not ours.
        Vm?.Scripts.MarkDragging(dragged);
        if (Vm is not null)
            Vm.StatusText = $"Moving {dragged.Name} — drop it on a folder, or anywhere else for the scripts root.";

        // The box that follows the pointer. Not a cursor because Avalonia doesn't offer one — see DragGhost.
        _ghost = new DragGhost(this);
        _ghost.Show(dragged.Name);

        // Awaited so both marks are released however the drag ends — dropped, cancelled with Esc, or let go
        // outside the window, where no DragLeave arrives to do it. The result also says whether a drop
        // happened at all: a completed move reports itself, so only a cancelled one has to take its own line
        // back down.
        var outcome = DragDropEffects.None;
        try { outcome = await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move); }
        finally
        {
            _ghost?.Dispose();
            _ghost = null;
            Vm?.Scripts.MarkDragging(null);
            ClearDropTarget();
            if (outcome == DragDropEffects.None && Vm is not null) Vm.StatusText = "";
        }
    }

    private void OnScriptDragOver(object? sender, DragEventArgs e)
    {
        var carrying = e.DataTransfer.Contains(ScriptPathFormat);
        e.DragEffects = carrying ? DragDropEffects.Move : DragDropEffects.None;
        // Both the folder rows and the tree itself route here, and the folder's handler marks the event
        // handled — so whichever call this is already tells us what a drop would hit right now.
        MarkDropTarget(carrying ? FolderOf(sender) : null);
        if (carrying) _ghost?.FollowPointer(e);
        e.Handled = true;
    }

    /// <summary>
    /// DragLeave bubbles up from every row the pointer crosses, so acting on it directly cleared the
    /// highlight between each DragOver and the next — which is what made the indicator flicker as the mouse
    /// moved. Only a pointer genuinely outside the tree's bounds counts as leaving.
    /// </summary>
    private void OnScriptDragLeave(object? sender, DragEventArgs e)
    {
        var p = e.GetPosition(ScriptsTree);
        if (p.X < 0 || p.Y < 0 || p.X > ScriptsTree.Bounds.Width || p.Y > ScriptsTree.Bounds.Height)
        {
            ClearDropTarget();
            _ghost?.Hide();   // no drop surface under the pointer, so there's nothing to follow
        }
    }

    private void MarkDropTarget(ScriptFolderViewModel? folder) => Vm?.Scripts.MarkDropTarget(folder);

    private void ClearDropTarget() => Vm?.Scripts.ClearDropTarget();

    private void OnScriptDropOnFolder(object? sender, DragEventArgs e)
    {
        ClearDropTarget();
        if (Vm is not null && FolderOf(sender) is { } folder && e.DataTransfer.TryGetValue(ScriptPathFormat) is string src)
        {
            Vm.Scripts.MoveScript(src, folder.FullPath);
            e.Handled = true;
        }
    }

    private void OnScriptDropOnRoot(object? sender, DragEventArgs e)
    {
        ClearDropTarget();
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
