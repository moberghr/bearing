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
using Bearing.App.Completion;
using Bearing.App.Editing;
using Bearing.App.Input;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Sql;
using TextMateSharp.Grammars;

namespace Bearing.App.Views;

public partial class MainWindow
{
    // ---- menu bar (Alt) + focus mode ----

    /// <summary>Esc unwinds, most-modal first: the menu bar → a running query.</summary>
    private bool HandleEscape()
    {
        if (Vm is null) return false;
        if (QuickPickOpen) { HideQuickPick(); return true; }
        if (PaletteOpen) { HidePalette(); return true; }
        if (_pendingPanel.IsOpen) { _pendingPanel.Hide(); return true; }
        if (Vm.IsMenuVisible) { Vm.IsMenuVisible = false; return true; }
        if (Vm.Execution.IsBusy) { Vm.Execution.CancelExecution(); return true; }
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
        if (Vm?.Workspace.SelectedTab is { } tab) Vm.Workspace.CloseTab(tab);
    }
    private async void OnMenuRenameTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Workspace.SelectedTab is { } tab) await RenameTabAsync(tab);
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
        r.Register(KeyCommand.Sync(CommandIds.TabNew, "New tab", KeyScope.Global, "File", () => Vm?.Workspace.NewTab()));
        r.Register(KeyCommand.Sync(CommandIds.TabClose, "Close tab", KeyScope.Global, "File",
            () => { if (Vm?.Workspace.SelectedTab is { } tab) Vm.Workspace.CloseTab(tab); }, canRun: () => Vm?.Workspace.SelectedTab is not null));
        r.Register(new KeyCommand(CommandIds.TabRename, "Rename tab…", KeyScope.Global, "File",
            async () => { if (Vm?.Workspace.SelectedTab is { } tab) await RenameTabAsync(tab); }, canRun: () => Vm?.Workspace.SelectedTab is not null));
        r.Register(KeyCommand.Sync(CommandIds.ViewToggleSidePane, "Toggle side pane", KeyScope.Global, "View",
            () => { if (Vm is not null) Vm.SidePaneOpen = !Vm.SidePaneOpen; }));
        r.Register(KeyCommand.Sync(CommandIds.ViewToggleResults, "Toggle results", KeyScope.Global, "View", ToggleResultsVisible));
        r.Register(KeyCommand.Sync(CommandIds.StatementPrev, "Previous statement", KeyScope.Global, "Editor", () => MoveToAdjacentStatement(-1)));
        r.Register(KeyCommand.Sync(CommandIds.StatementNext, "Next statement", KeyScope.Global, "Editor", () => MoveToAdjacentStatement(+1)));
        // Escape only claims the key when there's something to dismiss; otherwise it falls through.
        r.Register(KeyCommand.Sync(CommandIds.AppEscape, "Escape / cancel", KeyScope.Global, "View",
            () => HandleEscape(),
            canRun: () => Vm is not null && (AnyOverlayOpen || _pendingPanel.IsOpen || Vm.IsMenuVisible || Vm.Execution.IsBusy)));
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
        r.Register(KeyCommand.Sync(CommandIds.EditorDeleteToLineStart, "Delete to line start", KeyScope.Editor, "Editor",
            () => ApplyDelete(TextDeleter.ToLineStart)));
        r.Register(KeyCommand.Sync(CommandIds.EditorDeleteWordBack, "Delete word before caret", KeyScope.Editor, "Editor",
            () => ApplyDelete(TextDeleter.WordBefore)));

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

    /// <summary>The editor's (start, end) offsets: the selection when there is one, else the caret twice.</summary>
    private (int Start, int End) EditorSpan() => Editor.SelectionLength > 0
        ? (Editor.SelectionStart, Editor.SelectionStart + Editor.SelectionLength)
        : (Editor.CaretOffset, Editor.CaretOffset);

    /// <summary>Ctrl+U / Ctrl+W: apply a <see cref="TextDeleter"/> span as one document edit, so undo
    /// stays granular and the caret lands where the removed text began.</summary>
    private void ApplyDelete(Func<string, int, int, DeleteRange> op)
    {
        var (start, end) = EditorSpan();
        var range = op(Editor.Text, start, end);
        if (range.IsEmpty) return;

        Editor.TextArea.ClearSelection();
        Editor.Document.Remove(range.Start, range.Length);
        Editor.CaretOffset = range.Start;
        Editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>Ctrl+/: toggle <c>-- </c> comments over the lines the caret/selection touches.</summary>
    private void ToggleLineComment()
    {
        var (start, end) = EditorSpan();
        var result = Bearing.Sql.LineCommenter.Toggle(Editor.Text, start, end);
        if (result.Text == Editor.Text) return;

        Editor.Document.Replace(0, Editor.Document.TextLength, result.Text);
        Editor.SelectionStart = result.SelectionStart;
        Editor.SelectionLength = result.SelectionLength;
        Editor.CaretOffset = result.SelectionStart + result.SelectionLength;
    }

    /// <summary>Ctrl+Shift+A: select the whole statement the caret sits in.</summary>
    private void SelectCurrentQuery()
    {
        if (Bearing.Sql.StatementSplitter.StatementAt(Editor.Text, Editor.CaretOffset) is not { } stmt) return;
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
            if (Vm?.Workspace.SelectedTab is { } t) _tabMru.Use(t);
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
        // The pending-changes panel is modal too, but has no local key handler: swallow every global
        // shortcut under it EXCEPT Escape, which must reach the dispatcher so HandleEscape can close it.
        if (_pendingPanel.IsOpen && e.Key is not Key.Escape) return;
        // Alt-tap tracking: a lone Alt press arms the menu toggle (fired on key-up); any other key cancels it.
        _altAlone = e.Key is Key.LeftAlt or Key.RightAlt;
        _dispatcher.TryHandle(e, KeyScope.Global); // Global scope; Editor/Grid scopes are handled in their tunnels
    }

    private async Task RunAsync()
    {
        if (Vm is null) return;
        var selected = Editor.SelectedText;
        var sql = string.IsNullOrWhiteSpace(selected)
            ? Bearing.Sql.StatementSplitter.StatementAt(Editor.Text, Editor.CaretOffset)?.Text ?? Editor.Text
            : selected;
        // A selection (or whole buffer) may hold several blank-line-separated statements without
        // semicolons — normalize so they run as a batch instead of one malformed command.
        sql = Bearing.Sql.StatementSplitter.EnsureSeparated(sql);
        // Capture the tab the run belongs to: execution is per-tab, so the user may switch tabs while it
        // runs. Only refresh the visible grid if that tab is still selected — otherwise its results are
        // stored on its own VM and render when the user switches back.
        var ran = Vm.Workspace.SelectedTab;
        await Vm.Execution.ExecuteAsync(sql);
        if (ReferenceEquals(ran, Vm.Workspace.SelectedTab)) RebuildResults(ran);
    }

    /// <summary>query.runAll: run the entire buffer as a batch, ignoring caret/selection.</summary>
    private async Task RunAllAsync()
    {
        if (Vm is null) return;
        var ran = Vm.Workspace.SelectedTab;
        await Vm.Execution.ExecuteAsync(Bearing.Sql.StatementSplitter.EnsureSeparated(Editor.Text));
        if (ReferenceEquals(ran, Vm.Workspace.SelectedTab)) RebuildResults(ran);
    }

    /// <summary>tab.next / tab.prev: move to the adjacent tab in visual (strip) order, wrapping around.</summary>
    private void SelectAdjacentTab(int dir)
    {
        if (Vm is null || Vm.Workspace.Tabs.Count == 0) return;
        var i = Vm.Workspace.SelectedTab is { } t ? Vm.Workspace.Tabs.IndexOf(t) : 0;
        Vm.Workspace.SelectedTab = Vm.Workspace.Tabs[(i + dir + Vm.Workspace.Tabs.Count) % Vm.Workspace.Tabs.Count];
    }

    /// <summary>tab.mruNext / tab.mruPrev: cycle through tabs in most-recently-used order while Ctrl is
    /// held; releasing Ctrl (see <see cref="OnKeyUp"/>) commits the landed tab as most-recent.</summary>
    private void CycleMru(int dir)
    {
        if (Vm is null) return;
        _tabMru.Sync(Vm.Workspace.Tabs);
        var items = _tabMru.Items;
        if (items.Count < 2) return;
        if (!_mruCycling) { _mruCycling = true; _mruIndex = 0; }
        _mruIndex = (_mruIndex + dir + items.Count) % items.Count;
        Vm.Workspace.SelectedTab = items[_mruIndex];
    }

    /// <summary>tab.goto{n}: jump to tab n (1-based); n=9 is "last tab" (browser convention). Clamps.</summary>
    private void SelectTabByIndex(int n)
    {
        if (Vm is null || Vm.Workspace.Tabs.Count == 0) return;
        var idx = n >= 9 ? Vm.Workspace.Tabs.Count - 1 : System.Math.Min(n - 1, Vm.Workspace.Tabs.Count - 1);
        Vm.Workspace.SelectedTab = Vm.Workspace.Tabs[idx];
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
        ShowQuickPick("Select connection…", Vm.Connections.Connections.Select(c =>
            (c.Name, (Action)(() => Vm.Connections.SelectedTabConnection = c))).ToList());
    }

    private void OpenDatabasePicker()
    {
        if (Vm is null) return;
        ShowQuickPick("Select database…", Vm.Connections.TabDatabases.Select(d =>
            (d, (Action)(() => DatabasePicker.SelectedItem = d))).ToList());
    }

    private void OnWindowNavKey(object? sender, KeyEventArgs e)
    {
        // an overlay owns the keyboard while open (nav commands carry no Escape, so block all of them)
        if (AnyOverlayOpen || _pendingPanel.IsOpen) return;
        _dispatcher.TryHandle(e, KeyScope.Global, _navCommands);
    }

    /// <summary>Tunnel-phase Escape → cancel the selected tab's in-flight query, pre-empting the grid's
    /// clear-selection and AvaloniaEdit (which sit lower in the tunnel). Overlays / the pending-changes
    /// panel / the Alt menu own Escape first — they're dismissed by the bubble-phase <see cref="HandleEscape"/>,
    /// so we only claim it here once none of them are up.</summary>
    private void OnWindowEscapeCancel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Escape || Vm is null) return;
        if (AnyOverlayOpen || _pendingPanel.IsOpen || Vm.IsMenuVisible) return;
        if (Vm.Execution.IsBusy) { Vm.Execution.CancelExecution(); e.Handled = true; }
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
    private Control? SidebarFocusTarget() => Sidebar.FocusTarget;

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();

    private async Task OpenAsync()
    {
        if (Vm is null) return;
        if (await _dialogs.PickOpenScriptAsync(Vm.ScriptsDirectory) is { } path)
        {
            await Vm.Workspace.LoadScriptIntoSelectedAsync(path);
            LoadEditorFromSelectedTab();
        }
    }

    private async Task SaveAsync()
    {
        if (Vm is null) return;
        if (Vm.Workspace.SelectedTab?.ScriptPath is { } existing)
        {
            await Vm.Workspace.SaveSelectedScriptAsync(existing, Editor.Text);
            return;
        }
        await SaveAsAsync();
    }

    /// <summary>Always prompt for a destination (File ▸ Save As…), even for a file-backed tab.</summary>
    private async Task SaveAsAsync()
    {
        if (Vm is null) return;
        var suggested = System.IO.Path.GetFileName(Vm.Workspace.SelectedTab?.ScriptPath ?? "query.sql");
        if (await _dialogs.PickSaveScriptAsync(suggested, Vm.ScriptsDirectory) is { } path)
            await Vm.Workspace.SaveSelectedScriptAsync(path, Editor.Text);
    }

    /// <summary>Flush the live editor text/caret into the selected tab (called before close/save session).</summary>
    internal void FlushActiveEditor()
    {
        if (Vm?.Workspace.SelectedTab is { } tab)
        {
            tab.Text = Editor.Text;
            tab.CaretOffset = Editor.CaretOffset;
        }
    }

    /// <summary>Render the given tab's current result frame, plus the back-bar state (FK-nav history).</summary>
    internal void RebuildResults(EditorTabViewModel? tab)
    {
        _pendingPanel.Hide(); // a new run / tab switch invalidates the pending-changes panel
        ResultsView.CanGoBack = tab?.CanGoBack ?? false;
        ResultsView.Results = tab?.Results; // assignment triggers the rebuild (reads CanGoBack)
        SetResultsVisible(tab?.Results is { Count: > 0 }); // reveal on results, collapse when none
    }

}
