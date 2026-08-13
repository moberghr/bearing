using System.Threading.Tasks;
using Avalonia.Interactivity;
using Bearing.App.Input;

namespace Bearing.App.Views;

public partial class MainWindow
{
    // Shell-level connection & keybinding commands. The sidebar's own tree/scripts/history interactions
    // moved into Controls/SidebarView; these stay here because they're invoked from the menu, the command
    // palette, and the write-guard hook (not just the sidebar).

    private async void OnEditKeybindingsClick(object? sender, RoutedEventArgs e) => await EditKeybindingsAsync();

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
    /// command palette and by the sidebar's ＋ button (via SidebarView.AddConnectionRequested).</summary>
    private async Task AddConnectionAsync()
    {
        if (Vm is null) return;
        // As in SidebarView's edit path: a new connection's default credential kind depends on whether a
        // password can be stored, so ask again before deciding it from a probe that ran at startup.
        await Vm.RefreshSecretStorageAsync();
        var result = await _dialogs.ShowConnectionDialogAsync(null, null, (i, p, ct) => Vm.Connections.TestConnectionAsync(i, p, ct), Vm.SecretStorage);
        if (result is { Delete: false }) await Vm.Connections.AddOrUpdateConnectionAsync(result.Connection, result.Password);
    }
}
