using System.Threading.Tasks;
using Avalonia.Interactivity;
using Squirrel.App.Input;

namespace Squirrel.App.Views;

public partial class MainWindow
{
    // Shell-level connection & keybinding commands. The sidebar's own tree/scripts/history interactions
    // moved into Controls/SidebarView; these stay here because they're invoked from the menu, the command
    // palette, and the write-guard hook (not just the sidebar).

    private async void OnEditKeybindingsClick(object? sender, RoutedEventArgs e) => await EditKeybindingsAsync();

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
        var dialog = new ConnectionDialog(null, null, (i, p, ct) => Vm.TestConnectionAsync(i, p, ct), Vm.SecretStorageSecure);
        var result = await dialog.ShowDialog<ConnectionDialogResult?>(this);
        if (result is { Delete: false }) await Vm.AddOrUpdateConnectionAsync(result.Connection, result.Password);
    }

    /// <summary>Write-guard prompt for the VM: confirm a risky batch against a guarded connection.</summary>
    private Task<bool> ConfirmDangerousWriteAsync(
        Squirrel.Core.Data.ConnectionInfo connection, System.Collections.Generic.IReadOnlyList<string> verbs)
        => new ConfirmWriteDialog(connection, verbs).ShowDialog<bool>(this);
}
