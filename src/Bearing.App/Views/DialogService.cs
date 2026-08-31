using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Bearing.App.Editing;
using Bearing.App.Results;
using Bearing.App.Services;
using Bearing.Core.Data;

namespace Bearing.App.Views;

/// <summary>
/// The concrete <see cref="IDialogService"/>: owns construction of the app's dialogs and storage pickers,
/// parented to the current main window (resolved lazily — the shell view-model is built before the window
/// exists). Stateless, so any number of instances behave identically. With no window — headless runs and
/// tests — confirmations proceed and pickers return null, matching the pre-refactor "unset delegate =
/// proceed / nothing picked" behaviour.
/// </summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public Task<bool> ConfirmWriteAsync(WriteConfirmation request)
        => Owner is { } window
            ? new ConfirmWriteDialog(request).ShowDialog<bool>(window)
            : Task.FromResult(true);

    public Task<bool> ConfirmCancelRunningAsync(int runningCount, string? tabName = null)
        => Owner is { } window
            ? new ConfirmCancelRunningDialog(runningCount, tabName).ShowDialog<bool>(window)
            : Task.FromResult(true); // no window → proceed, as every other confirmation does here

    public Task<CloseChoice> ConfirmCloseTabAsync(string tabName)
        => Owner is { } window
            ? new ConfirmCloseDialog(tabName).ShowDialog<CloseChoice>(window)
            : Task.FromResult(CloseChoice.Discard); // no window → close as it did before the prompt existed

    public Task<bool> ConfirmDeleteScriptAsync(string fileName)
        => Owner is { } window
            ? new ConfirmDeleteScriptDialog(fileName).ShowDialog<bool>(window)
            : Task.FromResult(false); // no window → do nothing; a delete needs a real answer

    public Task<ProjectRemoval> ConfirmRemoveProjectAsync(string name, string directory)
        => Owner is { } window
            ? new ConfirmRemoveProjectDialog(name, directory).ShowDialog<ProjectRemoval>(window)
            : Task.FromResult(ProjectRemoval.Cancel); // no window → do nothing; a delete needs a real answer

    public Task<ConnectionDialogResult?> ShowConnectionDialogAsync(
        ConnectionInfo? existing,
        string? existingPassword,
        Func<ConnectionInfo, string?, CancellationToken, Task<bool>> test,
        SecretStoragePosture storage)
        => Owner is { } window
            ? new ConnectionDialog(existing, existingPassword, test, storage).ShowDialog<ConnectionDialogResult?>(window)
            : Task.FromResult<ConnectionDialogResult?>(null);

    public Task<string?> ShowTextPromptAsync(string prompt, string initial = "")
        => Owner is { } window
            ? new TextPromptDialog(prompt, initial).ShowDialog<string?>(window)
            : Task.FromResult<string?>(null);

    public async Task<string?> PickFolderAsync(string title, string? startDir = null)
    {
        if (Owner is not { } window) return null;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolder(window, startDir),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickOpenScriptAsync(string? startDir)
    {
        if (Owner is not { } window) return null;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL script",
            AllowMultiple = false,
            FileTypeFilter = new[] { SqlFileType },
            SuggestedStartLocation = await StartFolder(window, startDir),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickImportFileAsync(string? startDir)
    {
        if (Owner is not { } window) return null;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import connections from data-sources.json",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DBeaver connections") { Patterns = new[] { "*.json" } },
            },
            SuggestedStartLocation = await StartFolder(window, startDir),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveScriptAsync(string suggestedName, string? startDir)
    {
        if (Owner is not { } window) return null;
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save SQL script",
            DefaultExtension = "sql",
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[] { SqlFileType },
            SuggestedStartLocation = await StartFolder(window, startDir),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickExportFileAsync(string suggestedName, ExportFormat format)
    {
        if (Owner is not { } window) return null;
        var extension = ResultExport.Extension(format);
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export result to {ResultExport.Label(format)}",
            DefaultExtension = extension,
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[] { new FilePickerFileType(ResultExport.Label(format)) { Patterns = new[] { "*." + extension } } },
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Show SQL in a read-only, monospace preview window (selectable to copy), syntax-highlighted
    /// like every other SQL surface — this is a generated <c>CREATE TABLE</c>, a view's source, or the DML an
    /// inline edit is about to run, all of which are read closely enough to want colour. No word wrap: the
    /// window is wide and the text is pre-formatted, so wrapping would only break its alignment.</summary>
    public void ShowSqlPreview(string sql, string title = "SQL preview — changes to save")
    {
        if (Owner is not { } owner) return;
        var box = SqlViewer.Create(sql);

        var win = new Window
        {
            Title = title,
            Width = 720,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 8, 8) };
        close.Click += (_, _) => win.Close();
        DockPanel.SetDock(close, Dock.Bottom);

        var panel = new DockPanel();
        panel.Children.Add(close);
        panel.Children.Add(box);
        win.Content = panel;
        win.Show(owner);
    }

    private static async Task<IStorageFolder?> StartFolder(Window window, string? dir)
        => dir is not null ? await window.StorageProvider.TryGetFolderFromPathAsync(dir) : null;

    private static readonly FilePickerFileType SqlFileType = new("SQL scripts") { Patterns = new[] { "*.sql" } };
}
