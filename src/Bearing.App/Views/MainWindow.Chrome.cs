using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        var current = tab.IsScratch ? tab.DisplayName : tab.Header;
        var name = await _dialogs.ShowTextPromptAsync(tab.IsScratch ? "Rename tab" : "Rename script file", current);
        if (name is not null) await Vm.Workspace.RenameTabAsync(tab, name);
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
    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActivePanel = SidePanel.History;
    }

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
    private void OnMenuSchemaClick(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.ActivePanel = SidePanel.Schema; }
    private void OnMenuScriptsClick(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.ActivePanel = SidePanel.Scripts; }
    private void OnAboutClick(object? sender, RoutedEventArgs e) => AboutDialog.Open(this);

    // Rail tile clicked: activate that panel, or collapse the pane if its tile is re-clicked while open.
    private void OnRailTileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && (sender as Control)?.Tag is string tag && Enum.TryParse<SidePanel>(tag, out var panel))
            Vm.ActivateOrTogglePanel(panel);
    }
}
