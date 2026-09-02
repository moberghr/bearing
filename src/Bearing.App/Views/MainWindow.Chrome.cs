using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.Input;
using Bearing.App.Services;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

public partial class MainWindow
{
    // ---- tabs ----

    private void OnNewTabClick(object? sender, RoutedEventArgs e) => NewTabAndFocus();

    /// <summary>
    /// Point whichever row holds the selected tab at it.
    /// <para>
    /// Selection flows one way — the view model decides, the strips display (#67). Two strips cannot share a
    /// two-way <c>SelectedTab</c>: the row without the tab writes its own null back and unselects the other
    /// row. Nor can a strip's <c>SelectionChanged</c> simply be honoured — a strip auto-selects an item of its
    /// own when its items change, so pinning the selected tab made the row that lost it claim a different tab
    /// and push that into the view model. What the user clicked arrives through
    /// <see cref="OnTabStripPressed"/>, which is unambiguous; keyboard movement arrives through
    /// <see cref="OnTabStripSelectionChanged"/>, which is honoured only from the row that already holds the
    /// selection — the guard that keeps the auto-selection out.
    /// </para>
    /// <para>
    /// The other row is not cleared, because it cannot be: a <c>TabStrip</c> is always-selected and will
    /// re-assert one. It is stopped from <i>drawing</i> it instead — the <c>dormant</c> class in the XAML,
    /// bound to which row owns the selection.
    /// </para>
    /// </summary>
    private void SyncTabStripSelection()
    {
        var tab = Vm?.Workspace.SelectedTab;
        if (tab is null) return;
        Show(PinnedTabStrip, tab);
        Show(TabStrip, tab);

        static void Show(TabStrip strip, EditorTabViewModel tab)
        {
            if (Holds(strip, tab)) strip.SelectedItem = tab;
        }
    }

    /// <summary>
    /// Wire both tab strips' press handling. In code rather than XAML because it has to tunnel: a
    /// <c>TabStripItem</c> marks a press handled as it selects itself, which stops a bubbling handler on the
    /// strip — the strip would move its own selection and the view model would never hear about it.
    /// </summary>
    private void WireTabStrips()
    {
        foreach (var strip in new[] { PinnedTabStrip, TabStrip })
            strip.AddHandler(InputElement.PointerPressedEvent, OnTabStripPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Which button a press was, read from the window so it does not depend on a hit target.</summary>
    private static PointerUpdateKind PressKind(PointerPressedEventArgs e)
        => e.GetCurrentPoint(null).Properties.PointerUpdateKind;

    /// <summary>Whether a pressed visual is (or is inside) a tab's ✕.</summary>
    private static bool IsCloseAffordance(object? source)
        => source is Visual visual
           && visual.FindAncestorOfType<Border>(includeSelf: true) is { Tag: "close" };

    /// <summary>
    /// Whether a pressed visual is (or is inside) a tab's pin toggle.
    /// <para>
    /// Separate from <see cref="IsCloseAffordance"/> rather than folded into one "is an affordance" check,
    /// because the two do different things to the selection: closing must <b>not</b> select the tab first
    /// (#87's neighbour rule would then pick from the wrong index), while pinning a tab you are not on is a
    /// perfectly ordinary thing to want and selecting it would be a surprise. Both need the strip's tunnel
    /// handler to leave them alone; only the reason differs.
    /// </para>
    /// </summary>
    private static bool IsPinAffordance(object? source)
        => source is Visual visual
           && visual.FindAncestorOfType<Border>(includeSelf: true) is { Tag: "pin" };

    /// <summary>The tab a pressed visual belongs to, found by walking up to its container.</summary>
    private static (Control Target, EditorTabViewModel Tab)? Tab(object? source)
        => source is Visual visual
           && visual.FindAncestorOfType<TabStripItem>(includeSelf: true) is
           { DataContext: EditorTabViewModel tab } item
            ? (item, tab)
            : null;

    /// <summary>
    /// Keyboard navigation inside a focused strip, pushed to the view model.
    /// <para>
    /// Only from the row that <i>currently holds</i> the selection, and only when it actually moved. That is
    /// what keeps this from being the feedback loop a plain two-way binding was: a strip re-asserts a
    /// selection of its own whenever its items change, so pinning the selected tab made the row that lost it
    /// claim a different tab and write that back. The row that lost the tab no longer holds it, so its
    /// re-assertion is ignored; the row that gained it agrees with the view model already.
    /// </para>
    /// <para>Clicks do not come through here — they arrive at <see cref="OnTabStripPressed"/>, which can
    /// select across rows.</para>
    /// </summary>
    private void OnTabStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabStrip strip || Vm?.Workspace is not { } workspace) return;
        if (strip.SelectedItem is not EditorTabViewModel moved) return;
        if (ReferenceEquals(moved, workspace.SelectedTab)) return;
        if (workspace.SelectedTab is not { } held || !Holds(strip, held)) return;

        workspace.SelectedTab = moved;
    }

    /// <summary>Whether a strip's items include this tab — i.e. whether it is the row showing it.</summary>
    private static bool Holds(TabStrip strip, EditorTabViewModel tab)
        => strip.ItemsSource is IEnumerable items && items.Cast<object?>().Any(i => ReferenceEquals(i, tab));

    /// <summary>Open a tab and put the caret in it (#88). Opening a tab is only ever a prelude to typing in
    /// it, and the ＋ button and the keyboard command both left focus where it was — on the button, or
    /// wherever the keystroke came from — so the first thing typed went nowhere.
    /// <para>One helper for both routes so they cannot diverge; the view-model creates the tab and does not
    /// know about focus, which is the right split.</para></summary>
    /// <summary>Which tab a left-click on a header selects. The strips do not report their own selection
    /// (see <see cref="SyncTabStripSelection"/>), so this is where a click becomes a selection.</summary>
    private void SelectTabFromHeader(Control target, EditorTabViewModel tab, PointerPressedEventArgs e)
    {
        if (!TabPointerGestures.ActivatesCloseButton(e.GetCurrentPoint(target).Properties.PointerUpdateKind)) return;
        if (Vm?.Workspace is { } workspace) workspace.SelectedTab = tab;
    }

    internal void NewTabAndFocus(Guid? connectionId = null)
    {
        Vm?.Workspace.NewTab(connectionId: connectionId);
        Editor.TextArea.Focus();
    }

    /// <summary>
    /// The tab's pin toggle: pin an unpinned tab, unpin a pinned one.
    /// <para>
    /// Left button only, matching the ✕ — a right-click here is opening the tab's context menu (which has
    /// the same action as a menu item), and a middle-click is the header's close gesture.
    /// </para>
    /// </summary>
    private void OnPinTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: EditorTabViewModel tab } target) return;
        if (!TabPointerGestures.ActivatesCloseButton(e.GetCurrentPoint(target).Properties.PointerUpdateKind)) return;
        e.Handled = true;
        Vm?.Workspace.SetPinned(tab, !tab.IsPinned);
    }

    private async void OnCloseTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: EditorTabViewModel tab } target) return;
        // Left button only: a right-click here is opening the tab's context menu, and a middle-click is the
        // header's own close gesture below — closing on either meant the ✕ acted on presses that weren't
        // aimed at it (#66).
        if (!TabPointerGestures.ActivatesCloseButton(e.GetCurrentPoint(target).Properties.PointerUpdateKind)) return;
        e.Handled = true;
        await CloseTabAsync(tab);
    }

    /// <summary>
    /// A press anywhere on a tab: left selects it, middle closes it, as every tabbed app does. Closing is
    /// routed through the same <see cref="CloseTabAsync"/> as the ✕ and Ctrl+F4, so the unsaved-buffer prompt
    /// and the "last tab reopens an empty one" rule apply identically.
    /// <para>
    /// Handled on the <b>strip</b> rather than in the item template, because a <c>TabStripItem</c> has padding
    /// of its own: a press in that 10px margin missed a template-level handler while the strip still moved its
    /// own <c>SelectedIndex</c>, so the header lit up in a row whose tab was never selected — the editor, the
    /// results and the view model all stayed on the previous one, and nothing corrected it.
    /// </para>
    /// <para>
    /// And on the <b>tunnel</b>, wired in <see cref="WireTabStrips"/>: a <c>TabStripItem</c> marks the press
    /// handled when it selects itself, so a bubbling handler on the strip never ran at all.
    /// </para>
    /// </summary>
    private async void OnTabStripPressed(object? sender, PointerPressedEventArgs e)
    {
        // A left press on the ✕ is its own handler's: selecting the tab on the way past would leave the
        // neighbour rule (#87) picking from the wrong index. A *middle* press is not — the ✕ ignores
        // everything but the left button (#66), so returning here made the close gesture do nothing on the
        // one target it most obviously aims at, while working two pixels away.
        if (IsCloseAffordance(e.Source)
            && TabPointerGestures.ActivatesCloseButton(PressKind(e))) return;
        // Likewise the pin toggle, which has its own handler. Different reason from the ✕ above: pinning a
        // tab you are not currently on is an ordinary thing to want, and dragging the selection along with
        // it would be a surprise — a middle-click on the pin is still the header's close gesture, so the
        // same left-button-only condition applies.
        if (IsPinAffordance(e.Source)
            && TabPointerGestures.ActivatesCloseButton(PressKind(e))) return;
        if (Tab(e.Source) is not (var target, var tab)) return;
        SelectTabFromHeader(target, tab, e);
        if (!TabPointerGestures.ClosesTab(e.GetCurrentPoint(target).Properties.PointerUpdateKind)) return;
        // The inline rename box lives in this same panel, and on X11 a middle-click in a text box pastes the
        // primary selection — so closing the tab on that press would take the tab away mid-rename, from a
        // gesture that meant "paste".
        if (tab.IsRenaming || e.Source is TextBox) return;
        // A pinned tab is one you mean to keep, and its row has no ✕ for the same reason (#67). Unpin first.
        if (tab.IsPinned) return;

        e.Handled = true;
        await CloseTabAsync(tab);
    }

    private void OnTabHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: EditorTabViewModel tab }) BeginTabRename(tab);
    }

    /// <summary>
    /// A double-click on the empty part of the tab strip opens a new tab, as every browser does.
    /// <para>
    /// Guarded on the source, because this is wired to the strip's scroller and a double-click on a tab
    /// bubbles up through it — that gesture already means "rename this tab" (#39), and both firing would
    /// rename a tab and then open another one on top of the rename.
    /// </para>
    /// </summary>
    private void OnTabStripEmptyDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<TabStripItem>(includeSelf: true) is not null)
            return;
        e.Handled = true;
        NewTabAndFocus();   // the same call the + button makes, so focus lands in the new buffer
    }

    /// <summary>
    /// Turn a tab's header into an editable box and put the caret in it. The box is already in the template
    /// (hidden), so it can only be focused once a layout pass has made it visible — hence the posted lookup
    /// rather than a Focus() call here.
    /// </summary>
    private void BeginTabRename(EditorTabViewModel tab)
    {
        tab.BeginRename();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Both strips: the box lives in whichever row the tab is in, and looking only in the unpinned
            // one left a pinned rename with IsRenaming set and no box to clear it.
            var box = new[] { PinnedTabStrip, TabStrip }
                .SelectMany(strip => strip.GetVisualDescendants().OfType<TextBox>())
                .FirstOrDefault(b => ReferenceEquals(b.DataContext, tab));
            if (box is null) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private async void OnTabRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: EditorTabViewModel tab }) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await CommitTabRenameAsync(tab);
                break;
            case Key.Escape:
                e.Handled = true;
                tab.IsRenaming = false;
                Editor.TextArea.Focus();
                break;
        }
    }

    /// <summary>Clicking away commits, as an inline edit should — the box disappearing with the typing thrown
    /// away reads as a bug. Guarded on <c>IsRenaming</c>, which the commit clears before it awaits, so the
    /// focus loss that follows can't come back round for a second rename.</summary>
    private async void OnTabRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: EditorTabViewModel tab } && tab.IsRenaming)
            await CommitTabRenameAsync(tab);
    }

    private async Task CommitTabRenameAsync(EditorTabViewModel tab)
    {
        var name = tab.RenameDraft.Trim();
        tab.IsRenaming = false;
        if (Vm is null || name.Length == 0) return;
        await Vm.Workspace.RenameTabAsync(tab, name);
    }

    /// <summary>
    /// Right-click on a tab title: build that tab's menu and show it at the pointer. Built per click, so
    /// "can this be saved?" is evaluated now rather than being a stale answer from when the tab appeared.
    /// </summary>
    private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: EditorTabViewModel tab } host) return;

        // The editor control holds the selected tab's live buffer, so both the Save action and the
        // is-there-anything-to-save test have to see what's on screen, not the last-synced text.
        FlushActiveEditor();
        var path = tab.ScriptPath;

        var menu = TabContextMenu.Build(
            tab,
            rename: () => { BeginTabRename(tab); return Task.CompletedTask; },
            save: () => SaveTabAsync(tab),
            canSave: path is null || tab.IsModified,
            close: () => CloseTabAsync(tab),
            reveal: path is null ? null : () => vm.RevealScript(path),
            openFolder: path is null ? null : () => OpenContainingFolderAsync(path),
            delete: path is null ? null : () => DeleteTabFileAsync(tab),
            togglePin: () => vm.Workspace.SetPinned(tab, !tab.IsPinned),
            renameGesture: MenuGesture(CommandIds.TabRename),
            saveGesture: MenuGesture(CommandIds.FileSave),
            closeGesture: MenuGesture(CommandIds.TabClose),
            pinGesture: MenuGesture(CommandIds.TabTogglePin));

        menu.ShowAt(host, showAtPointer: true);
        e.Handled = true;
    }

    /// <summary>Save one tab — which may not be the selected one, so it can't go through
    /// <see cref="SaveAsync"/>. A tab with no file yet needs a destination first.</summary>
    private async Task SaveTabAsync(EditorTabViewModel tab)
    {
        if (Vm is null) return;
        FlushActiveEditor();   // the selected tab's buffer lives in the editor control, not on the tab
        if (tab.ScriptPath is { } existing)
        {
            await Vm.Workspace.SaveScriptAsync(tab, existing, tab.Text);
            return;
        }
        if (await _dialogs.PickSaveScriptAsync($"{tab.DisplayName}.sql", Vm.ScriptsDirectory) is { } chosen)
            await Vm.Workspace.SaveScriptAsync(tab, chosen, tab.Text);
    }

    /// <summary>Delete a tab's backing file, behind a confirm, closing the tab with it.</summary>
    private async Task DeleteTabFileAsync(EditorTabViewModel tab)
    {
        if (Vm is null || tab.ScriptPath is not { } path) return;
        if (!await _dialogs.ConfirmDeleteScriptAsync(Path.GetFileName(path))) return;
        if (Vm.Workspace.DeleteScript(path)) LoadEditorFromSelectedTab();
    }

    /// <summary>Show a script in the OS file manager, selected. The reveal swallows its own failures and just
    /// reports false (no file manager, or launching one isn't permitted), so say so rather than dropping it.</summary>
    private async Task OpenContainingFolderAsync(string path)
    {
        if (!await FileReveal.OpenContainingFolderAsync(path) && Vm is { } vm)
            vm.StatusText = $"Could not open the folder for {Path.GetFileName(path)}.";
    }

    // ---- side pane ----

    private void OnToggleSidePane(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.SidePaneOpen = !Vm.SidePaneOpen;
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
        var name = await _dialogs.ShowTextPromptAsync("Project name", current);
        if (name is not null) await Vm.RenameProjectAsync(name);
    }

    private async void OnRemoveProjectClick(object? sender, RoutedEventArgs e) => await RemoveCurrentProjectAsync();

    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (await _dialogs.PickFolderAsync("Open Bearing project folder", Vm.ProjectBrowseDirectory) is { } path)
            await Vm.OpenProjectAsync(path);
    }

    private async void OnNewProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (await _dialogs.PickFolderAsync("Choose an empty folder for the new project", Vm.ProjectBrowseDirectory) is not { } path) return;
        var name = await _dialogs.ShowTextPromptAsync("Project name", new System.IO.DirectoryInfo(path).Name);
        if (name is not null) await Vm.NewProjectAsync(path, name);
    }

    // ---- run / open / save / history ----

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Execution.IsBusy == true) { Vm.Execution.CancelExecution(); return; }
        await RunAsync();
    }

    /// <summary>Re-render the visuals that read a type-scale token once rather than binding it (#52).</summary>
    internal void RefreshTypeScale() => ResultsView.RefreshTypeScale();

    private async void OnExportRunClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.Execution.ExportRunAsync();
    }

    // The toolbar History button now reveals the inline History side-panel (design §4) instead of a window.
    private void OnHistoryClick(object? sender, RoutedEventArgs e) => Vm?.ShowPanel(SidePanel.History);

    private async void OnSettingsClick(object? sender, RoutedEventArgs e) => await OpenSettingsAsync();

    // ---- database pill selection (driven in code; async ItemsSource defeats a plain binding) ----
    private bool _syncingDb;

    private void SyncDbPicker()
    {
        if (Vm is null) return;
        _syncingDb = true;
        DatabasePicker.SelectedItem = Vm.Connections.SelectedTabDatabase; // matched by value; null → placeholder
        _syncingDb = false;
    }

    private void OnDatabaseSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingDb || Vm is null) return;
        if (DatabasePicker.SelectedItem is string db) Vm.Connections.SelectedTabDatabase = db;
    }

    // ---- server (connection) pill selection ----
    //
    // Driven in code for the same reason as the database pill, plus one this pill has on its own: a
    // `SelectedItem` binding cannot repair a selection the ComboBox has *lost*. `RefreshConnections()`
    // rebuilds the list with Clear() + N adds, which drops the rendered selection, and the
    // `OnPropertyChanged(nameof(SelectedTabConnection))` that follows re-pushes the value the binding
    // already holds — a no-op. That left the pill on its "— server —" placeholder while the tab, the
    // accent, and the database pill all still knew the connection, and a tab switch (which only re-raises
    // the same property) could not heal it either.
    //
    // SyncConnectionPicker assigns null *first*, so every sync is a real change and any lost selection is
    // restored. Write-back also no longer nulls the tab's connection when the list is cleared: only a
    // ConnectionInfo the user actually picked is pushed to the view-model.
    private bool _syncingConnection;
    private bool _connectionSyncQueued;

    private void SyncConnectionPicker()
    {
        if (Vm is null) return;
        if (ConnectionPicker.IsDropDownOpen) return;   // don't yank the list out from under the user
        _syncingConnection = true;
        ConnectionPicker.SelectedItem = null;          // force a change even when the value is unchanged
        ConnectionPicker.SelectedItem = Vm.Connections.SelectedTabConnection;
        _syncingConnection = false;
    }

    /// <summary>
    /// Sync once the current notification has unwound. Same hazard as <c>OnTabDatabasesChanged</c> — the
    /// ComboBox's own ItemsSourceView subscribes after this window does, so assigning a selection from
    /// inside a CollectionChanged (or from inside the SelectionChanged this assignment itself raises)
    /// re-enters the selection model over a list it still reports the old contents of.
    /// </summary>
    private void QueueConnectionPickerSync()
    {
        if (_connectionSyncQueued) return;
        _connectionSyncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _connectionSyncQueued = false;
            SyncConnectionPicker();
        });
    }

    private void OnConnectionsListChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => QueueConnectionPickerSync();

    private void OnConnectionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingConnection || Vm is null) return;
        // Only a real pick writes back. A Clear() during a list rebuild also raises this with a null
        // selection, and treating that as the user choosing "no connection" would unset the tab's server.
        if (ConnectionPicker.SelectedItem is Bearing.Core.Data.ConnectionInfo c)
            Vm.Connections.SelectedTabConnection = c;
    }

    // ---- menu & rail click handlers ----

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e) => await SaveAsAsync();
    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();
    private async void OnCloseCurrentTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Workspace.SelectedTab is { } tab) await CloseTabAsync(tab);
    }
    private void OnMenuRenameTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Workspace.SelectedTab is { } tab) BeginTabRename(tab);
    }
    private void OnMenuSchemaClick(object? sender, RoutedEventArgs e) => Vm?.ShowPanel(SidePanel.Schema);
    private void OnMenuScriptsClick(object? sender, RoutedEventArgs e) => Vm?.ShowPanel(SidePanel.Scripts);
    private void OnAboutClick(object? sender, RoutedEventArgs e) => AboutDialog.Open(this);

    // Rail tile clicked: activate that panel, or collapse the pane if its tile is re-clicked while open.
    private void OnRailTileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && (sender as Control)?.Tag is string tag && Enum.TryParse<SidePanel>(tag, out var panel))
            Vm.ActivateOrTogglePanel(panel);
    }
}
