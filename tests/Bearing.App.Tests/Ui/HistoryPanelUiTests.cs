using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The history panel's connection filter and grouping toggle, on the real window: that the two controls are
/// there, show what the view model holds, and write the user's choice back. What they look like is not
/// asserted here (§4.3).
/// </summary>
[Collection(UiTestCollection.Name)]
public class HistoryPanelUiTests
{
    private readonly UiTestSession _ui;

    public HistoryPanelUiTests(UiTestSession ui) => _ui = ui;

    private static T Named<T>(ShellHarness shell, string name) where T : Control
        => shell.Window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task<ShellHarness> ShowHistoryAsync(string name)
    {
        var shell = await ShellHarness.ShowAsync(name);
        await shell.Vm.Connections.AddOrUpdateConnectionAsync(
            new ConnectionInfo { Id = Guid.NewGuid(), Name = "prod", ProviderId = "postgres", Database = "app" }, null);
        shell.Vm.ShowPanel(SidePanel.History);
        shell.Pump();
        await shell.Vm.History.ReloadAsync(default);
        shell.Pump();
        return shell;
    }

    [Fact]
    public Task The_connection_dropdown_lists_the_projects_connections_and_writes_the_pick_back() => _ui.Run(async () =>
    {
        using var shell = await ShowHistoryAsync("history-connection-filter");
        var dropdown = Named<ComboBox>(shell, "HistoryConnectionFilter");

        Assert.True(dropdown.IsEffectivelyVisible);
        Assert.Equal(["All connections", "prod"], dropdown.Items.Cast<HistoryConnectionOption>().Select(o => o.Label));
        Assert.Equal(0, dropdown.SelectedIndex);

        dropdown.SelectedIndex = 1;
        shell.Pump();

        Assert.Equal("prod", shell.Vm.History.SelectedConnection?.Label);

        // And the other way: the view model's choice is what the control shows.
        shell.Vm.History.SelectedConnection = HistoryConnectionOption.All;
        shell.Pump();

        Assert.Equal(0, dropdown.SelectedIndex);
    });

    [Fact]
    public Task The_grouping_pills_show_the_grouping_and_write_the_pick_back() => _ui.Run(async () =>
    {
        using var shell = await ShowHistoryAsync("history-grouping");
        var byDay = Named<RadioButton>(shell, "HistoryGroupByDay");
        var byConnection = Named<RadioButton>(shell, "HistoryGroupByConnection");

        Assert.True(byDay.IsEffectivelyVisible);
        Assert.True(byDay.IsChecked);
        Assert.False(byConnection.IsChecked);

        byConnection.IsChecked = true;
        shell.Pump();

        Assert.Equal(HistoryGrouping.Connection, shell.Vm.History.Grouping);
        Assert.False(byDay.IsChecked);

        shell.Vm.History.Grouping = HistoryGrouping.Day;
        shell.Pump();

        Assert.True(byDay.IsChecked);
    });
}
