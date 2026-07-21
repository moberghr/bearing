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

public partial class MainWindow
{
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

}
