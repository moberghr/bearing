using System;
using System.IO;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Showing a side panel has to do two things — switch to it and reveal the pane — and for a while it only
/// did one. Revealing was a side effect of <c>OnActivePanelChanged</c>, but <c>[ObservableProperty]</c>'s
/// setter short-circuits on an unchanged value, so "Show Scripts" with the pane collapsed and Scripts already
/// active never ran the handler and did nothing at all. Panel *switching* still worked, which is why the
/// symptom read as "it only changes focus".
/// <para>
/// The rail tile's toggle-on-re-click is the deliberate exception and is pinned here too, so a later "make
/// them consistent" pass has to argue with a test. What the pane looks like once revealed is still eyeball-QA
/// (§4.3) — this covers the state machine, which is where the bug was.
/// </para>
/// </summary>
public class SidePanelRevealTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-panel", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm() => new(
        new FakeProvider(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FakeSecretStore(),
        settings: SettingsService.InMemory(new AppSettings()));

    // ---- the bug -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(SidePanel.Schema)]
    [InlineData(SidePanel.Scripts)]
    [InlineData(SidePanel.History)]
    public void Show_panel_reveals_a_collapsed_pane_even_when_that_panel_is_already_active(SidePanel panel)
    {
        var vm = NewVm();
        vm.ShowPanel(panel);
        vm.SidePaneOpen = false;   // user collapsed it (rail re-click / Toggle side pane)

        vm.ShowPanel(panel);       // "Show <panel>" again — the no-op case

        Assert.True(vm.SidePaneOpen);
        Assert.Equal(panel, vm.ActivePanel);
    }

    [Fact]
    public void Show_panel_switches_and_reveals_from_collapsed()
    {
        var vm = NewVm();
        vm.SidePaneOpen = false;

        vm.ShowPanel(SidePanel.Scripts);

        Assert.True(vm.SidePaneOpen);
        Assert.Equal(SidePanel.Scripts, vm.ActivePanel);
    }

    [Fact]
    public void Show_panel_never_collapses_an_open_pane()
    {
        var vm = NewVm();
        vm.ShowPanel(SidePanel.Scripts);

        vm.ShowPanel(SidePanel.Scripts);   // repeat invocations are idempotent, not a toggle

        Assert.True(vm.SidePaneOpen);
    }

    // ---- why every caller must go through ShowPanel -----------------------------------------------

    [Fact]
    public void Setting_ActivePanel_alone_does_not_reveal_the_pane()
    {
        // Pins the reason ShowPanel exists: reveal is no longer a change-notification side effect, so a bare
        // assignment is not a "show". If this starts passing as a reveal, the implicit open is back.
        var vm = NewVm();
        vm.SidePaneOpen = false;

        vm.ActivePanel = SidePanel.History;

        Assert.False(vm.SidePaneOpen);
        Assert.Equal(SidePanel.History, vm.ActivePanel);
    }

    // ---- the rail tile keeps its toggle ------------------------------------------------------------

    [Fact]
    public void Rail_tile_re_click_collapses_the_pane()
    {
        var vm = NewVm();
        vm.ActivateOrTogglePanel(SidePanel.Scripts);
        Assert.True(vm.SidePaneOpen);

        vm.ActivateOrTogglePanel(SidePanel.Scripts);

        Assert.False(vm.SidePaneOpen);
        Assert.Equal(SidePanel.Scripts, vm.ActivePanel);   // still the active one, just hidden
    }

    [Fact]
    public void Rail_tile_on_a_collapsed_pane_reopens_it_rather_than_toggling_again()
    {
        var vm = NewVm();
        vm.ActivateOrTogglePanel(SidePanel.Scripts);
        vm.ActivateOrTogglePanel(SidePanel.Scripts);   // collapsed

        vm.ActivateOrTogglePanel(SidePanel.Scripts);   // same tile again — the pane is what changed, so reopen

        Assert.True(vm.SidePaneOpen);
    }

    [Fact]
    public void Rail_tile_for_a_different_panel_switches_without_collapsing()
    {
        var vm = NewVm();
        vm.ActivateOrTogglePanel(SidePanel.Scripts);

        vm.ActivateOrTogglePanel(SidePanel.History);

        Assert.True(vm.SidePaneOpen);
        Assert.Equal(SidePanel.History, vm.ActivePanel);
    }
}
