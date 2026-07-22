using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Squirrel.App.Services;
using Squirrel.Core.Data;

namespace Squirrel.App.Views;

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

    public Task<bool> ConfirmWriteAsync(ConnectionInfo connection, IReadOnlyList<string> verbs)
        => Owner is { } window
            ? new ConfirmWriteDialog(connection, verbs).ShowDialog<bool>(window)
            : Task.FromResult(true);

    public Task<ConnectionDialogResult?> ShowConnectionDialogAsync(
        ConnectionInfo? existing,
        string? existingPassword,
        Func<ConnectionInfo, string?, CancellationToken, Task<bool>> test,
        bool secretStorageSecure)
        => Owner is { } window
            ? new ConnectionDialog(existing, existingPassword, test, secretStorageSecure).ShowDialog<ConnectionDialogResult?>(window)
            : Task.FromResult<ConnectionDialogResult?>(null);

    public Task<string?> ShowTextPromptAsync(string prompt, string initial = "")
        => Owner is { } window
            ? new TextPromptDialog(prompt, initial).ShowDialog<string?>(window)
            : Task.FromResult<string?>(null);

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner is not { } window) return null;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
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

    /// <summary>Show SQL in a read-only, monospace preview window (selectable to copy).</summary>
    public void ShowSqlPreview(string sql, string title = "SQL preview — changes to save")
    {
        if (Owner is not { } owner) return;
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
