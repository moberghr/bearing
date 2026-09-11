using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The activity panel as a realized control in the running shell (#101).
/// <para>
/// The claims here are the ones no pure test can hold: that the panel is actually in the visual tree and
/// shown for its own rail position, and that its polling is tied to being on screen through the real
/// <c>ShellViewModel</c> rather than through a property a test set by hand. §0.2 — none of this says it looks
/// right; the two-line rows and the splitter need eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ActivityPanelUiTests
{
    private readonly UiTestSession _ui;

    public ActivityPanelUiTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Showing_the_panel_reveals_it_and_starts_it_polling() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync("activity-panel");
        var panel = Panel(shell);

        // Another panel is showing to begin with, so this one is present but hidden and idle.
        Assert.False(panel.IsVisible);
        Assert.False(shell.Vm.Activity.IsPolling);

        shell.Vm.ShowPanel(SidePanel.Activity);
        shell.Pump();

        Assert.True(panel.IsVisible);
        Assert.True(shell.Vm.Activity.IsPolling);

        // And its list is realized, which is what F6 has to be able to reach.
        Assert.NotNull(panel.FindControl<ListBox>("BackendList"));
    });

    [Fact]
    public Task Leaving_the_panel_stops_it_polling() => _ui.Run(async () =>
    {
        // The only thing in the app that queries a server on a timer, so it must not keep doing it for a
        // panel nobody is looking at.
        using var shell = await ShellHarness.ShowAsync("activity-stop");

        shell.Vm.ShowPanel(SidePanel.Activity);
        shell.Pump();
        Assert.True(shell.Vm.Activity.IsPolling);

        shell.Vm.ShowPanel(SidePanel.History);
        shell.Pump();
        Assert.False(shell.Vm.Activity.IsPolling);
    });

    [Fact]
    public Task Collapsing_the_pane_from_the_activity_tile_and_reopening_it_resumes_polling() => _ui.Run(async () =>
    {
        // The trap this panel is built around. [ObservableProperty]'s setter short-circuits on an unchanged
        // value, so toggling the pane from the Activity tile never changes ActivePanel — and polling hung off
        // OnActivePanelChanged alone would stop on the collapse and never start again, leaving the panel
        // visible and frozen.
        using var shell = await ShellHarness.ShowAsync("activity-toggle");

        shell.Vm.ActivateOrTogglePanel(SidePanel.Activity);
        shell.Pump();
        Assert.True(shell.Vm.SidePaneOpen);
        Assert.True(shell.Vm.Activity.IsPolling);

        shell.Vm.ActivateOrTogglePanel(SidePanel.Activity);   // same tile again: collapses the pane
        shell.Pump();
        Assert.False(shell.Vm.SidePaneOpen);
        Assert.Equal(SidePanel.Activity, shell.Vm.ActivePanel);   // …and ActivePanel did not change
        Assert.False(shell.Vm.Activity.IsPolling);

        shell.Vm.ActivateOrTogglePanel(SidePanel.Activity);
        shell.Pump();
        Assert.True(shell.Vm.SidePaneOpen);
        Assert.True(shell.Vm.Activity.IsPolling);
    });

    [Fact]
    public Task The_rail_carries_a_tile_for_it() => _ui.Run(async () =>
    {
        // The rail's click handler is generic over the enum, so the tile is the whole registration — and a
        // panel with no tile is only reachable from the palette.
        using var shell = await ShellHarness.ShowAsync("activity-rail");

        var tile = shell.Window.GetVisualDescendants().OfType<RadioButton>()
            .FirstOrDefault(r => r.Tag as string == nameof(SidePanel.Activity));

        Assert.NotNull(tile);
    });

    private static ActivityPanelView Panel(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<ActivityPanelView>().Single();
}
