using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bearing.App.Editing;
using Bearing.App.Input;
using Bearing.App.ViewModels;
using Bearing.Sql;

namespace Bearing.App.Views;

public partial class MainWindow
{
    /// <summary>Register every Global and Editor command. Ids and default gestures live in
    /// <see cref="KeymapDefaults"/>; this is where each id gets its behavior and applicability guard.
    /// Grid-scoped commands register themselves from <c>ResultView</c> into this same registry.</summary>
    private void RegisterCommands(CommandRegistry r)
    {
        // ---- Global ----
        r.Register(new KeyCommand(CommandIds.Run, "Run", KeyScope.Global, "Query", async () => await RunAsync()));
        r.Register(KeyCommand.Sync(CommandIds.CompletionTrigger, "Trigger completion", KeyScope.Global, "Editor", () => _completion.TriggerExplicit()));
        r.Register(new KeyCommand(CommandIds.FileSave, "Save", KeyScope.Global, "File", async () => await SaveAsync()));
        r.Register(new KeyCommand(CommandIds.FileSaveAs, "Save As…", KeyScope.Global, "File", async () => await SaveAsAsync()));
        r.Register(new KeyCommand(CommandIds.FileOpen, "Open…", KeyScope.Global, "File", async () => await OpenAsync()));
        r.Register(KeyCommand.Sync(CommandIds.TabNew, "New tab", KeyScope.Global, "File", () => Vm?.Workspace.NewTab()));
        r.Register(new KeyCommand(CommandIds.TabClose, "Close tab", KeyScope.Global, "File",
            async () => { if (Vm?.Workspace.SelectedTab is { } tab) await CloseTabAsync(tab); }, canRun: () => Vm?.Workspace.SelectedTab is not null));
        r.Register(new KeyCommand(CommandIds.TabRename, "Rename tab…", KeyScope.Global, "File",
            async () => { if (Vm?.Workspace.SelectedTab is { } tab) await RenameTabAsync(tab); }, canRun: () => Vm?.Workspace.SelectedTab is not null));
        r.Register(KeyCommand.Sync(CommandIds.ViewToggleSidePane, "Toggle side pane", KeyScope.Global, "View",
            () => { if (Vm is not null) Vm.SidePaneOpen = !Vm.SidePaneOpen; }));
        r.Register(KeyCommand.Sync(CommandIds.ViewToggleResults, "Toggle results", KeyScope.Global, "View", ToggleResultsVisible));
        r.Register(KeyCommand.Sync(CommandIds.StatementPrev, "Previous statement", KeyScope.Global, "Editor", () => _text.MoveToAdjacentStatement(-1)));
        r.Register(KeyCommand.Sync(CommandIds.StatementNext, "Next statement", KeyScope.Global, "Editor", () => _text.MoveToAdjacentStatement(+1)));
        // Escape only claims the key when there's something to dismiss; otherwise it falls through.
        r.Register(KeyCommand.Sync(CommandIds.AppEscape, "Escape / cancel", KeyScope.Global, "View",
            () => HandleEscape(),
            canRun: () => Vm is not null && (_palette.AnyOpen || Vm.IsMenuVisible || Vm.Execution.IsBusy)));
        r.Register(KeyCommand.Sync(CommandIds.PaletteOpen, "Command palette", KeyScope.Global, "View",
            () => { if (Vm is not null) _palette.TogglePalette(); }));
        r.Register(KeyCommand.Sync(CommandIds.TabNext, "Next tab (visual order)", KeyScope.Global, "Tabs", () => WithWorkspace(ws => _tabs.SelectAdjacent(ws, +1))));
        r.Register(KeyCommand.Sync(CommandIds.TabPrev, "Previous tab (visual order)", KeyScope.Global, "Tabs", () => WithWorkspace(ws => _tabs.SelectAdjacent(ws, -1))));
        r.Register(KeyCommand.Sync(CommandIds.TabMruNext, "Next tab (recently used)", KeyScope.Global, "Tabs", () => WithWorkspace(ws => _tabs.CycleMru(ws, +1))));
        r.Register(KeyCommand.Sync(CommandIds.TabMruPrev, "Previous tab (recently used)", KeyScope.Global, "Tabs", () => WithWorkspace(ws => _tabs.CycleMru(ws, -1))));
        for (var n = 1; n <= 9; n++)
        {
            var i = n; // capture
            r.Register(KeyCommand.Sync(CommandIds.TabGoto(i), i == 9 ? "Go to last tab" : $"Go to tab {i}", KeyScope.Global, "Tabs",
                () => WithWorkspace(ws => _tabs.SelectByIndex(ws, i))));
        }
        r.Register(KeyCommand.Sync(CommandIds.FocusCycle, "Cycle focus (editor / results / sidebar)", KeyScope.Global, "View", CycleFocus));
        r.Register(KeyCommand.Sync(CommandIds.FocusEditor, "Focus editor", KeyScope.Global, "View", () => Editor.TextArea.Focus()));
        r.Register(KeyCommand.Sync(CommandIds.FocusResults, "Focus results", KeyScope.Global, "View", FocusResultsPane));
        r.Register(KeyCommand.Sync(CommandIds.SelectProject, "Select project…", KeyScope.Global, "Connection", OpenProjectPicker));
        r.Register(KeyCommand.Sync(CommandIds.SelectConnection, "Select connection…", KeyScope.Global, "Connection", OpenConnectionPicker));
        r.Register(KeyCommand.Sync(CommandIds.SelectDatabase, "Select database…", KeyScope.Global, "Connection", OpenDatabasePicker));
        // ShowPanel, not a bare ActivePanel assignment: these must reveal a collapsed pane even when the
        // requested panel is already the active one (see ShellViewModel.ShowPanel).
        r.Register(KeyCommand.Sync(CommandIds.PanelConnections, "Show Connections panel", KeyScope.Global, "View",
            () => Vm?.ShowPanel(SidePanel.Schema)));
        r.Register(KeyCommand.Sync(CommandIds.PanelScripts, "Show Scripts panel", KeyScope.Global, "View",
            () => Vm?.ShowPanel(SidePanel.Scripts)));
        r.Register(KeyCommand.Sync(CommandIds.PanelHistory, "Show History panel", KeyScope.Global, "View",
            () => Vm?.ShowPanel(SidePanel.History)));
        r.Register(new KeyCommand(CommandIds.ConnectionNew, "New connection…", KeyScope.Global, "Connection", async () => await AddConnectionAsync()));
        r.Register(new KeyCommand(CommandIds.QueryRunAll, "Run entire script", KeyScope.Global, "Query", async () => await RunAllAsync()));
        r.Register(new KeyCommand(CommandIds.SettingsKeybindings, "Keyboard shortcuts…", KeyScope.Global, "View", async () => await EditKeybindingsAsync()));
        r.Register(new KeyCommand(CommandIds.SettingsOpen, "Settings…", KeyScope.Global, "View", async () => await OpenSettingsAsync()));

        // ---- Editor ----
        r.Register(KeyCommand.Sync(CommandIds.EditorOpenLineBelow, "Open line below", KeyScope.Editor, "Editor", () => _text.OpenLine(below: true)));
        r.Register(KeyCommand.Sync(CommandIds.EditorOpenLineAbove, "Open line above", KeyScope.Editor, "Editor", () => _text.OpenLine(below: false)));
        r.Register(KeyCommand.Sync(CommandIds.EditorToggleComment, "Toggle comment", KeyScope.Editor, "Editor", _text.ToggleLineComment));
        r.Register(KeyCommand.Sync(CommandIds.EditorSelectStatement, "Select statement", KeyScope.Editor, "Editor", _text.SelectCurrentStatement));
        r.Register(KeyCommand.Sync(CommandIds.EditorFoldCurrent, "Fold current", KeyScope.Editor, "Editor", () => _folding.FoldCurrent()));
        r.Register(KeyCommand.Sync(CommandIds.EditorUnfoldCurrent, "Unfold current", KeyScope.Editor, "Editor", () => _folding.UnfoldCurrent()));
        r.Register(KeyCommand.Sync(CommandIds.EditorFoldAll, "Fold all", KeyScope.Editor, "Editor", () => _folding.FoldAll()));
        r.Register(KeyCommand.Sync(CommandIds.EditorUnfoldAll, "Unfold all", KeyScope.Editor, "Editor", () => _folding.UnfoldAll()));
        r.Register(KeyCommand.Sync(CommandIds.EditorDeleteToLineStart, "Delete to line start", KeyScope.Editor, "Editor",
            () => _text.ApplyDelete(TextDeleter.ToLineStart)));
        r.Register(KeyCommand.Sync(CommandIds.EditorDeleteWordBack, "Delete word before caret", KeyScope.Editor, "Editor",
            () => _text.ApplyDelete(TextDeleter.WordBefore)));
        r.Register(KeyCommand.Sync(CommandIds.EditorZoomIn, "Zoom in (this tab)", KeyScope.Editor, "Editor", () => Zoom(z => z.ZoomIn())));
        r.Register(KeyCommand.Sync(CommandIds.EditorZoomOut, "Zoom out (this tab)", KeyScope.Editor, "Editor", () => Zoom(z => z.ZoomOut())));
        r.Register(KeyCommand.Sync(CommandIds.EditorZoomReset, "Reset zoom (this tab)", KeyScope.Editor, "Editor", () => Zoom(z => z.Reset())));

        // Navigation/focus commands are claimed in a window tunnel handler so the framework's own tab
        // traversal and the editor/grid don't swallow them first.
        _navCommands = new HashSet<string>
        {
            CommandIds.TabNext, CommandIds.TabPrev, CommandIds.TabMruNext, CommandIds.TabMruPrev,
            CommandIds.FocusCycle, CommandIds.FocusEditor, CommandIds.FocusResults,
            CommandIds.SelectProject, CommandIds.SelectConnection, CommandIds.SelectDatabase,
        };
        for (var n = 1; n <= 9; n++) _navCommands.Add(CommandIds.TabGoto(n));

        // Same set minus focus.editor, used while the caret is already in the editor — see OnWindowNavKey.
        _navCommandsFromEditor = new HashSet<string>(_navCommands);
        _navCommandsFromEditor.Remove(CommandIds.FocusEditor);
    }

    /// <summary>Run a zoom command on the selected tab and say what happened — a one-point change is easy
    /// to miss, and the status line is the only affordance the zoom has.</summary>
    private void Zoom(Action<EditorZoomController> change)
    {
        change(_zoom);
        if (Vm is { } vm) vm.StatusText = $"Editor font {_zoom.CurrentSize:0.#} pt (this tab)";
    }

    /// <summary>Run a workspace-scoped action, skipped before a view-model exists.</summary>
    private void WithWorkspace(Action<WorkspaceViewModel> act)
    {
        if (Vm is not null) act(Vm.Workspace);
    }

    // ---- key routing -------------------------------------------------------------------------

    /// <summary>
    /// Editor-scoped editing shortcuts, handled in the tunnel phase so they win over AvaloniaEdit's
    /// own handling of Enter / '/' / brackets. App-level shortcuts (Run, Save, …) stay in <see cref="OnKeyDown"/>.
    /// </summary>
    private void OnEditorKeyDown(object? sender, KeyEventArgs e) => _dispatcher.TryHandle(e, KeyScope.Editor);

    // Tracks whether Alt was pressed on its own (no other key during the hold) → a "tap" toggles the menu.
    private bool _altAlone;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // While an overlay (palette / quick-pick) is up it owns the keyboard — don't fire globals under it.
        if (_palette.AnyOpen) return;
        // Alt-tap tracking: a lone Alt press arms the menu toggle (fired on key-up); any other key cancels it.
        _altAlone = e.Key is Key.LeftAlt or Key.RightAlt;
        _dispatcher.TryHandle(e, KeyScope.Global); // Global scope; Editor/Grid scopes are handled in their tunnels
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // Releasing the modifier the MRU binding holds ends the cycle and commits the landed tab as
        // most-recent. Which modifier that is comes from the keymap (tab.mruNext is rebindable).
        if (Vm is not null && _tabs.EndsCycle(e.Key)) _tabs.EndCycle(Vm.Workspace);
        if (e.Key is Key.LeftAlt or Key.RightAlt && _altAlone && Vm is not null)
        {
            _altAlone = false;
            Vm.IsMenuVisible = !Vm.IsMenuVisible;
            if (Vm.IsMenuVisible) Dispatcher.UIThread.Post(() => MainMenu.Focus()); // enable keyboard menu nav
        }
    }

    private void OnWindowNavKey(object? sender, KeyEventArgs e)
    {
        // an overlay owns the keyboard while open (nav commands carry no Escape, so block all of them)
        if (_palette.AnyOpen) return;

        // Nav commands are claimed in the window's tunnel phase, i.e. *before* the editor's own tunnel
        // handler — which is why editor.zoomReset never saw Ctrl+0: focus.editor shares that gesture and
        // was swallowing it here. Focusing the editor is a no-op when the caret is already in it, so drop
        // that one claim in that case and let the editor's binding have the key.
        var claimed = Editor.TextArea.IsKeyboardFocusWithin ? _navCommandsFromEditor : _navCommands;
        _dispatcher.TryHandle(e, KeyScope.Global, claimed);
    }

    /// <summary>Tunnel-phase Escape → cancel the selected tab's in-flight query, pre-empting the grid's
    /// clear-selection and AvaloniaEdit (which sit lower in the tunnel). Overlays / the Alt menu own Escape
    /// first — they're dismissed by the bubble-phase <see cref="HandleEscape"/>, so we only claim it here
    /// once none of them are up.</summary>
    private void OnWindowEscapeCancel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Escape || Vm is null) return;
        if (_palette.AnyOpen || Vm.IsMenuVisible) return;
        if (Vm.Execution.IsBusy) { Vm.Execution.CancelExecution(); e.Handled = true; }
    }

    /// <summary>Esc unwinds, most-modal first: an overlay → the menu bar → a running query.</summary>
    private bool HandleEscape()
    {
        if (Vm is null) return false;
        if (_palette.HideTopmost()) return true;
        if (Vm.IsMenuVisible) { Vm.IsMenuVisible = false; return true; }
        if (Vm.Execution.IsBusy) { Vm.Execution.CancelExecution(); return true; }
        return false;
    }

    /// <summary>A press anywhere outside the menu bar dismisses it. Clicks on an open submenu land on a
    /// separate popup top-level, so they never reach this window handler — only genuine outside clicks do.</summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm?.IsMenuVisible == true && e.Source is Visual v && !FocusRing.IsWithin(v, MainMenu))
            Vm.IsMenuVisible = false;
    }

    /// <summary>Invoking a leaf menu item (one that does something, not a submenu header) closes the bar.</summary>
    private void OnMenuItemInvoked(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && e.Source is MenuItem { ItemCount: 0 }) Vm.IsMenuVisible = false;
    }

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

    // ---- focus & panes -----------------------------------------------------------------------

    /// <summary>view.toggleResults (Ctrl+R): flip the results pane; hiding it drops focus back to the editor.</summary>
    private void ToggleResultsVisible()
    {
        if (_resultsPane.Toggle()) Editor.TextArea.Focus();
    }

    private void FocusResultsPane()
    {
        if (_resultsPane.IsVisible) ResultsView.FocusableGrid?.Focus();
    }

    /// <summary>focus.cycle (F6): move keyboard focus editor → results grid → sidebar → editor, skipping
    /// regions that aren't currently shown. Detection uses the whole ResultsView/sidebar tree so a focused
    /// inner element (a grid cell presenter) still classifies as its region.</summary>
    private void CycleFocus()
    {
        var regions = new List<(Control Focus, Visual Container)> { (Editor.TextArea, Editor) };
        if (_resultsPane.IsVisible && ResultsView.FocusableGrid is { } grid) regions.Add((grid, ResultsView));
        if (Sidebar.FocusTarget is { } side) regions.Add((side, side));
        FocusRing.Cycle(TopLevel.GetTopLevel(this), regions);
    }

    // select.project / connection / database: open a filterable quick-pick (type to filter, ↑/↓, Enter).
    private void OpenProjectPicker()
    {
        if (Vm is null) return;
        _palette.ShowQuickPick("Select project…", Vm.RecentProjects.Select(p =>
            (p.Name, (Action)(() => ProjectCombo.SelectedItem = p))).ToList());
    }

    private void OpenConnectionPicker()
    {
        if (Vm is null) return;
        _palette.ShowQuickPick("Select connection…", Vm.Connections.Connections.Select(c =>
            (c.Name, (Action)(() => Vm.Connections.SelectedTabConnection = c))).ToList());
    }

    private void OpenDatabasePicker()
    {
        if (Vm is null) return;
        _palette.ShowQuickPick("Select database…", Vm.Connections.TabDatabases.Select(d =>
            (d, (Action)(() => DatabasePicker.SelectedItem = d))).ToList());
    }

    // ---- run / open / save / tabs ------------------------------------------------------------

    private async Task RunAsync()
    {
        if (Vm is null) return;
        // Capture the tab the run belongs to: execution is per-tab, so the user may switch tabs while it
        // runs. Only refresh the visible grid if that tab is still selected — otherwise its results are
        // stored on its own VM and render when the user switches back.
        var ran = Vm.Workspace.SelectedTab;
        await Vm.Execution.ExecuteAsync(_text.SqlToRun());
        if (ReferenceEquals(ran, Vm.Workspace.SelectedTab)) RebuildResults(ran);
    }

    /// <summary>query.runAll: run the entire buffer as a batch, ignoring caret/selection.</summary>
    private async Task RunAllAsync()
    {
        if (Vm is null) return;
        var ran = Vm.Workspace.SelectedTab;
        await Vm.Execution.ExecuteAsync(_text.SqlToRunAll());
        if (ReferenceEquals(ran, Vm.Workspace.SelectedTab)) RebuildResults(ran);
    }

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

    /// <summary>The one close path for every entry point (Ctrl+F4, File ▸ Close Tab, the tab strip's ✕).
    /// Flushes the live editor into the selected tab first — only that tab's buffer lives in the control,
    /// so without this a "Save" on close would write the last-synced text, not what's on screen.</summary>
    private async Task CloseTabAsync(EditorTabViewModel tab)
    {
        if (Vm is null) return;
        FlushActiveEditor();
        if (await Vm.Workspace.CloseTabAsync(tab)) LoadEditorFromSelectedTab();
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
        ResultsView.CanGoBack = tab?.CanGoBack ?? false;
        ResultsView.Results = tab?.Results; // assignment triggers the rebuild (reads CanGoBack)
        _resultsPane.SetVisible(tab?.Results is { Count: > 0 }); // reveal on results, collapse when none
    }
}
