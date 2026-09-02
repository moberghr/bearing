using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Which tab is selected after closing one (#87). The selection landed on the first tab, and the reason it
/// did is not visible in the view-model alone: <c>SelectedTab</c> is bound two-way to the tab strip's
/// <c>SelectedItem</c>, so removing the item makes the control fix up its own selection and write that back
/// through the binding <i>during</i> the removal. The old guard then saw a <c>SelectedTab</c> that was no
/// longer the closing tab, concluded it had nothing to do, and left the control's pick in place.
/// <para>
/// So the interesting fixture here is a fake of that control: a handler on the tabs collection that resets
/// the selection to the first tab whenever the list changes. With no view attached the old code passed, which
/// is exactly why it shipped.
/// </para>
/// </summary>
public class CloseTabSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-close-sel", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private async Task<ShellViewModel> Project([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var vm = new ShellViewModel(
            new ProviderRegistry(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            dialogs: new FakeDialogs(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        await vm.InitializeAsync(Path.Combine(_root, name));
        return vm;
    }

    /// <summary>Four tabs named so the assertions read as positions.</summary>
    private static EditorTabViewModel[] FourTabs(ShellViewModel vm)
    {
        vm.Workspace.Tabs.Clear();
        return Enumerable.Range(1, 4).Select(i => vm.Workspace.NewTab($"-- {i}")).ToArray();
    }

    /// <summary>
    /// Stand in for the bound TabStrip: on any change to the tabs collection, claim the first tab as the
    /// selection, the way a SelectingItemsControl fixes itself up and writes back through a two-way binding.
    /// </summary>
    private static void AttachSelectionClobberer(ShellViewModel vm)
        => ((INotifyCollectionChanged)vm.Workspace.Tabs).CollectionChanged += (_, _) =>
        {
            if (vm.Workspace.Tabs.Count > 0) vm.Workspace.SelectedTab = vm.Workspace.Tabs[0];
        };

    [Fact]
    public async Task Closing_a_tab_selects_the_one_to_its_left()
    {
        var vm = await Project();
        var tabs = FourTabs(vm);
        vm.Workspace.SelectedTab = tabs[2];

        Assert.True(await vm.Workspace.CloseTabAsync(tabs[2]));

        Assert.Same(tabs[1], vm.Workspace.SelectedTab);
    }

    [Fact]
    public async Task Closing_the_leftmost_tab_selects_the_one_to_its_right()
    {
        // There is nothing to the left, and the tab that took its place is the sensible answer.
        var vm = await Project();
        var tabs = FourTabs(vm);
        vm.Workspace.SelectedTab = tabs[0];

        Assert.True(await vm.Workspace.CloseTabAsync(tabs[0]));

        Assert.Same(tabs[1], vm.Workspace.SelectedTab);
    }

    [Fact]
    public async Task Closing_a_run_of_tabs_keeps_walking_left()
    {
        // The reason left beats right: closing repeatedly from one keystroke position keeps moving instead
        // of parking on the last tab.
        var vm = await Project();
        var tabs = FourTabs(vm);
        vm.Workspace.SelectedTab = tabs[3];

        await vm.Workspace.CloseTabAsync(tabs[3]);
        Assert.Same(tabs[2], vm.Workspace.SelectedTab);
        await vm.Workspace.CloseTabAsync(tabs[2]);
        Assert.Same(tabs[1], vm.Workspace.SelectedTab);
        await vm.Workspace.CloseTabAsync(tabs[1]);
        Assert.Same(tabs[0], vm.Workspace.SelectedTab);
    }

    /// <summary>The reported bug: with the bound control writing its own selection back mid-removal, the
    /// workspace still lands on the neighbour rather than leaving the control's first-tab pick in place.</summary>
    [Fact]
    public async Task A_control_that_reselects_during_the_removal_does_not_win()
    {
        var vm = await Project();
        var tabs = FourTabs(vm);
        vm.Workspace.SelectedTab = tabs[2];
        AttachSelectionClobberer(vm);

        Assert.True(await vm.Workspace.CloseTabAsync(tabs[2]));

        Assert.Same(tabs[1], vm.Workspace.SelectedTab);
        Assert.NotSame(tabs[0], vm.Workspace.SelectedTab);
    }

    /// <summary>Closing a tab that isn't the selected one leaves the selection alone — even with the control
    /// interfering, which is the case that made the old guard look like it worked.</summary>
    [Fact]
    public async Task Closing_an_unselected_tab_leaves_the_selection_where_it_was()
    {
        var vm = await Project();
        var tabs = FourTabs(vm);
        vm.Workspace.SelectedTab = tabs[3];

        Assert.True(await vm.Workspace.CloseTabAsync(tabs[1]));

        Assert.Same(tabs[3], vm.Workspace.SelectedTab);
    }

    [Fact]
    public async Task Closing_the_last_tab_opens_a_fresh_one_and_selects_it()
    {
        var vm = await Project();
        vm.Workspace.Tabs.Clear();
        var only = vm.Workspace.NewTab();

        Assert.True(await vm.Workspace.CloseTabAsync(only));

        var replacement = Assert.Single(vm.Workspace.Tabs);
        Assert.NotSame(only, replacement);
        Assert.Same(replacement, vm.Workspace.SelectedTab);
    }
}
