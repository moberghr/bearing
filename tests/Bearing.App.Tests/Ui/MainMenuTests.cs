using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bearing.App.Connections;
using Bearing.App.Input;
using Bearing.App.Views;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The in-window menu bar (<c>MainMenu</c>) — the one an Alt tap reveals on Windows and Linux. Its File menu
/// once carried only "From DBeaver…" while the macOS bar and the sidebar had gained export and import-from-file
/// (#201), so a Windows user could not find Export there.
/// </summary>
[Collection(UiTestCollection.Name)]
public class MainMenuTests
{
    private readonly UiTestSession _ui;

    public MainMenuTests(UiTestSession ui) => _ui = ui;

    private static Menu MainMenu(ShellHarness shell)
        => shell.Window.GetLogicalDescendants().OfType<Menu>().Single(m => m.Name == "MainMenu");

    private static MenuItem FileMenu(ShellHarness shell)
        => MainMenu(shell).Items.OfType<MenuItem>().Single(i => i.Header as string == "_File");

    private static IEnumerable<MenuItem> AllItems(MenuItem root)
        => root.GetLogicalDescendants().OfType<MenuItem>();

    private static MenuItem Item(MenuItem root, string header)
        => AllItems(root).Single(i => i.Header as string == header);

    [Fact]
    public Task File_menu_has_export_and_import_from_a_Bearing_export_file() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(File_menu_has_export_and_import_from_a_Bearing_export_file));
        var file = FileMenu(shell);

        Assert.Equal(CommandIds.ConnectionExport, Item(file, "Export connections…").Tag);
        var import = Item(file, "Import connections");
        Assert.Equal(CommandIds.ConnectionImportFile, Item(import, "From a Bearing export file…").Tag);
        // The same order the sidebar's two import lists use.
        Assert.Equal(
            ["From DBeaver…", "From a Bearing export file…", "From the installed Bearing…"],
            import.Items.OfType<MenuItem>().Select(i => i.Header as string));
    });

    [Fact]
    public Task Clicking_Export_connections_asks_the_save_picker() => _ui.Run(async () =>
    {
        var dialogs = new FakeDialogs();
        using var shell = await ShellHarness.ShowAsync(nameof(Clicking_Export_connections_asks_the_save_picker),
            dialogs: dialogs);
        // Export says "nothing to export" before it asks for a file when the project is empty.
        await shell.Vm.Connections.AddOrUpdateConnectionAsync(
            new ConnectionInfo { Id = Guid.NewGuid(), Name = "prod", ProviderId = "postgres", Database = "app" }, null);

        Item(FileMenu(shell), "Export connections…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shell.Pump();

        var suggested = Assert.Single(dialogs.ConnectionsExportPicks);
        Assert.Equal($"{shell.Vm.CurrentProjectName} connections.{ConnectionExportFile.Extension}", suggested);
    });

    [Fact]
    public Task Clicking_import_from_a_Bearing_export_file_asks_the_open_picker() => _ui.Run(async () =>
    {
        var dialogs = new FakeDialogs();
        using var shell = await ShellHarness.ShowAsync(
            nameof(Clicking_import_from_a_Bearing_export_file_asks_the_open_picker), dialogs: dialogs);

        Item(FileMenu(shell), "From a Bearing export file…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shell.Pump();

        Assert.Equal(1, dialogs.ConnectionsFilePicks);
        Assert.Empty(dialogs.ConnectionsExportPicks);
    });

    /// <summary>Every connection import/export the macOS bar's File menu offers, the in-window File menu offers
    /// too — the drift #201 left behind.</summary>
    [Fact]
    public Task Every_connection_import_and_export_in_the_macOS_File_menu_is_in_MainMenu() => _ui.Run(async () =>
    {
        HashSet<string> native;
        AppMenuBar.Enabled = true;
        try
        {
            using var mac = await ShellHarness.ShowAsync(nameof(Every_connection_import_and_export_in_the_macOS_File_menu_is_in_MainMenu) + "-mac");
            var fileMenu = NativeMenu.GetMenu(mac.Window)!.Items.OfType<NativeMenuItem>()
                .Single(i => i.Header == "File").Menu!;
            native = fileMenu.Items.OfType<NativeMenuItem>()
                .Select(i => i.CommandParameter as string)
                .OfType<string>()
                .Where(IsConnectionTransfer)
                .ToHashSet();
        }
        finally
        {
            AppMenuBar.Enabled = false;
            NativeMenu.SetMenu(Avalonia.Application.Current!, null);
        }

        using var shell = await ShellHarness.ShowAsync(nameof(Every_connection_import_and_export_in_the_macOS_File_menu_is_in_MainMenu));
        var inWindow = AllItems(FileMenu(shell)).Select(i => i.Tag as string).OfType<string>().ToHashSet();

        // Not vacuous: the bar does carry them (DBeaver, file, export).
        Assert.Equal(3, native.Count);
        Assert.Empty(native.Except(inWindow));
    });

    private static bool IsConnectionTransfer(string id)
        => id.StartsWith("connection.import", StringComparison.Ordinal)
           || id.StartsWith("connection.export", StringComparison.Ordinal);
}
