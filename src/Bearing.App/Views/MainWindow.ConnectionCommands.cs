using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Bearing.App.Input;
using Bearing.Core.Data;
using Bearing.Persistence.Import;

namespace Bearing.App.Views;

public partial class MainWindow
{
    // Shell-level connection & keybinding commands. The sidebar's own tree/scripts/history interactions
    // moved into Controls/SidebarView; these stay here because they're invoked from the menu, the command
    // palette, and the write-guard hook (not just the sidebar).

    private async void OnEditKeybindingsClick(object? sender, RoutedEventArgs e) => await EditKeybindingsAsync();

    private async void OnImportDBeaverClick(object? sender, RoutedEventArgs e) => await ImportFromDBeaverAsync();

    /// <summary>settings.open: the application settings dialog. Edits apply and persist as they're made,
    /// so there is nothing to save here; the window only reports back when the user asked to jump to the
    /// keyboard-shortcuts editor instead.</summary>
    private async Task OpenSettingsAsync()
    {
        if (Vm is null) return;
        var toKeybindings = await new SettingsWindow(Vm.SettingsService).ShowDialog<bool>(this);
        if (toKeybindings) await EditKeybindingsAsync();
    }

    /// <summary>settings.keybindings: edit the keymap, then persist the minimal diff, apply it live to the
    /// dispatcher, and refresh the menu gestures.</summary>
    private async Task EditKeybindingsAsync()
    {
        var defaults = KeymapDefaults.Build();
        var edited = await new KeybindingsWindow(_dispatcher.Keymap, defaults, _commands.All).ShowDialog<Keymap?>(this);
        if (edited is null) return;
        KeymapLoader.SaveOverrides(KeymapDiff.ComputeOverrides(defaults, edited.Bindings));
        _dispatcher.Keymap = edited;   // ResultView shares this dispatcher, so the grid picks it up too
        SyncMenuGestures();
        if (Vm is not null) Vm.StatusText = "Keyboard shortcuts updated.";
    }

    /// <summary>connection.new: open the connection dialog for a brand-new connection. Invoked by the
    /// command palette and by the sidebar's ＋ button (via SidebarView.AddConnectionRequested).
    /// <paramref name="folder"/> files the result straight into the folder the user opened it from (#80) —
    /// the dialog itself has no folder field, because the tree is where filing happens.</summary>
    private async Task AddConnectionAsync(string? folder = null)
    {
        if (Vm is null) return;
        // As in SidebarView's edit path: a new connection's default credential kind depends on whether a
        // password can be stored, so ask again before deciding it from a probe that ran at startup.
        await Vm.RefreshSecretStorageAsync();
        var result = await _dialogs.ShowConnectionDialogAsync(
            null, null, (i, p, ct) => Vm.Connections.TestConnectionAsync(i, p, ct),
            Vm.SecretStorage, Vm.Connections.Providers);
        if (result is not { Delete: false }) return;
        var connection = folder is null ? result.Connection : result.Connection with { Folder = folder };
        await Vm.Connections.AddOrUpdateConnectionAsync(connection, result.Password);
    }

    /// <summary>
    /// connection.import.dbeaver: pick a DBeaver workspace, review what it holds, and import the Postgres
    /// connections from it (#72).
    ///
    /// <para>Discovery is offered but never required — the workspace location is user-configurable and the
    /// Enterprise builds use different data directories — so a discovered project and "browse for the file"
    /// end at the same place. With exactly one project found the pick is skipped: a one-item list is a click
    /// that answers its own question.</para>
    /// </summary>
    private async Task ImportFromDBeaverAsync()
    {
        if (Vm is null) return;

        var found = DBeaverWorkspaces.Discover();
        switch (found.Count)
        {
            case 0:
                await BrowseAndImportAsync(null);
                return;
            case 1:
                await ImportDBeaverFileAsync(found[0].DataSourcesPath);
                return;
            default:
                var items = found
                    .Select(f => (f.Label, (Action)(() => _ = ImportDBeaverFileAsync(f.DataSourcesPath))))
                    .Append(("Browse…", (Action)(() =>
                        _ = BrowseAndImportAsync(Path.GetDirectoryName(found[0].DataSourcesPath)))))
                    .ToList();
                _palette.ShowQuickPick("Import from which DBeaver workspace?", items);
                return;
        }
    }

    private async Task BrowseAndImportAsync(string? startDir)
    {
        if (await _dialogs.PickImportFileAsync(startDir) is { } path) await ImportDBeaverFileAsync(path);
    }

    private async Task ImportDBeaverFileAsync(string path)
    {
        if (Vm is null) return;

        string json;
        try { json = await File.ReadAllTextAsync(path); }
        catch (Exception ex)
        {
            Vm.StatusText = $"Could not read {Path.GetFileName(path)}: {SafeErrorText.Of(ex)}";
            return;
        }

        var parsed = DBeaverImport.Parse(json);
        var choice = await new ImportConnectionsDialog(parsed, path).ShowDialog<ImportChoice?>(this);
        if (choice is null || choice.Connections.Count == 0) return;

        // Only the folders the chosen connections actually land in — importing the whole folder map would
        // leave behind empty folders from the parts of the workspace the user declined.
        var folders = choice.Connections
            .Select(c => c.Folder)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Vm.Connections.ImportConnectionsAsync(choice.Connections, folders, choice.UpdateExisting);
    }

}
