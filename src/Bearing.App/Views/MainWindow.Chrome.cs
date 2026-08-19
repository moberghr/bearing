using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Bearing.App.Controls;
using Bearing.App.Input;
using Bearing.App.Services;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

public partial class MainWindow
{
    // ---- tabs ----

    private void OnNewTabClick(object? sender, RoutedEventArgs e) => Vm?.Workspace.NewTab();

    private async void OnCloseTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: EditorTabViewModel tab }) { e.Handled = true; await CloseTabAsync(tab); }
    }

    private async void OnTabHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: EditorTabViewModel tab }) await RenameTabAsync(tab);
    }

    private async Task RenameTabAsync(EditorTabViewModel tab)
    {
        if (Vm is null) return;
        // Prefill what the tab shows: its file name (which is its header) without the .sql, or the
        // placeholder label while it has no file yet.
        var current = tab.ScriptPath is { } path ? Path.GetFileNameWithoutExtension(path) : tab.DisplayName;
        var name = await _dialogs.ShowTextPromptAsync(tab.IsScratch ? "Rename tab" : "Rename script file", current);
        if (name is not null) await Vm.Workspace.RenameTabAsync(tab, name);
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
            rename: () => RenameTabAsync(tab),
            save: () => SaveTabAsync(tab),
            canSave: path is null || tab.IsModified,
            close: () => CloseTabAsync(tab),
            reveal: path is null ? null : () => vm.RevealScript(path),
            openFolder: path is null ? null : () => OpenContainingFolderAsync(path),
            delete: path is null ? null : () => DeleteTabFileAsync(tab),
            renameGesture: MenuGesture(CommandIds.TabRename),
            saveGesture: MenuGesture(CommandIds.FileSave),
            closeGesture: MenuGesture(CommandIds.TabClose));

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
        if (await _dialogs.PickFolderAsync("Open Bearing project folder") is { } path)
            await Vm.OpenProjectAsync(path);
    }

    private async void OnNewProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (await _dialogs.PickFolderAsync("Choose an empty folder for the new project") is not { } path) return;
        var name = await _dialogs.ShowTextPromptAsync("Project name", new System.IO.DirectoryInfo(path).Name);
        if (name is not null) await Vm.NewProjectAsync(path, name);
    }

    // ---- run / open / save / history ----

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Execution.IsBusy == true) { Vm.Execution.CancelExecution(); return; }
        await RunAsync();
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

    // ---- menu & rail click handlers ----

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e) => await SaveAsAsync();
    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();
    private async void OnCloseCurrentTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Workspace.SelectedTab is { } tab) await CloseTabAsync(tab);
    }
    private async void OnMenuRenameTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Workspace.SelectedTab is { } tab) await RenameTabAsync(tab);
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
