using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Input;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Pinning tabs (#67): the split into two rows, what navigation then means, and what survives a restart.
/// <para>
/// Pinned tabs get their own row rather than being packed into the strip as browsers do, because a query
/// tool wants the scripts you keep coming back to at a fixed position — one the churn of scratch buffers
/// below never moves.
/// </para>
/// </summary>
public class PinnedTabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-pins", Guid.NewGuid().ToString("N"));

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

    private static EditorTabViewModel[] FourTabs(WorkspaceViewModel workspace)
    {
        workspace.Tabs.Clear();
        return Enumerable.Range(1, 4).Select(i => workspace.NewTab($"-- {i}")).ToArray();
    }

    // ---- the split ------------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_pinned_to_begin_with()
    {
        var vm = await Project();
        var workspace = vm.Workspace;
        FourTabs(workspace);

        Assert.Empty(workspace.PinnedTabs);
        Assert.Equal(4, workspace.UnpinnedTabs.Count);
        Assert.False(workspace.HasPinnedTabs);   // the row is hidden, not empty
    }

    [Fact]
    public async Task Pinning_moves_a_tab_to_the_pinned_row()
    {
        var vm = await Project();
        var workspace = vm.Workspace;
        var tabs = FourTabs(workspace);

        workspace.SetPinned(tabs[2], true);

        Assert.Equal([tabs[2]], workspace.PinnedTabs);
        Assert.Equal([tabs[0], tabs[1], tabs[3]], workspace.UnpinnedTabs);
        Assert.True(workspace.HasPinnedTabs);
        // Tabs stays the single source of truth, in its original order.
        Assert.Equal(tabs, workspace.Tabs);
    }

    [Fact]
    public async Task Unpinning_puts_it_back_in_its_original_position()
    {
        // Not at the end: the strip order follows Tabs, which pinning never reorders.
        var vm = await Project();
        var workspace = vm.Workspace;
        var tabs = FourTabs(workspace);

        workspace.SetPinned(tabs[1], true);
        workspace.SetPinned(tabs[1], false);

        Assert.Empty(workspace.PinnedTabs);
        Assert.Equal(tabs, workspace.UnpinnedTabs);
    }

    [Fact]
    public async Task Pinned_tabs_keep_their_relative_order()
    {
        var vm = await Project();
        var workspace = vm.Workspace;
        var tabs = FourTabs(workspace);

        workspace.SetPinned(tabs[3], true);
        workspace.SetPinned(tabs[1], true);

        // Tabs order, not pin order — otherwise a pinned tab's position depends on when you pinned it.
        Assert.Equal([tabs[1], tabs[3]], workspace.PinnedTabs);
    }

    [Fact]
    public async Task A_new_tab_lands_in_the_unpinned_row()
    {
        var vm = await Project();
        var workspace = vm.Workspace;
        var tabs = FourTabs(workspace);
        workspace.SetPinned(tabs[0], true);

        var fresh = workspace.NewTab("-- new");

        Assert.DoesNotContain(fresh, workspace.PinnedTabs);
        Assert.Contains(fresh, workspace.UnpinnedTabs);
    }

    [Fact]
    public async Task Closing_a_pinned_tab_takes_it_out_of_the_row()
    {
        var vm = await Project();
        var workspace = vm.Workspace;
        var tabs = FourTabs(workspace);
        workspace.SetPinned(tabs[0], true);

        Assert.True(await workspace.CloseTabAsync(tabs[0]));

        Assert.Empty(workspace.PinnedTabs);
        Assert.False(workspace.HasPinnedTabs);
    }

    [Fact]
    public async Task Pinning_a_tab_that_is_not_open_does_nothing()
    {
        var vm = await Project();
        var workspace = vm.Workspace;
        FourTabs(workspace);
        var stranger = new EditorTabViewModel("elsewhere.sql");

        workspace.SetPinned(stranger, true);

        Assert.Empty(workspace.PinnedTabs);
        Assert.False(stranger.IsPinned);
    }

    // ---- navigation -----------------------------------------------------------------------------

    [Fact]
    public async Task Stepping_follows_the_drawn_order_pinned_first()
    {
        // tab.next has to walk the strip as drawn, or it appears to jump rows at random.
        var vm = await Project();
        var workspace = vm.Workspace;
        var tabs = FourTabs(workspace);
        workspace.SetPinned(tabs[2], true);

        Assert.Equal([tabs[2], tabs[0], tabs[1], tabs[3]], TabNavigator.VisualOrder(workspace));
    }

    [Fact]
    public async Task Goto_one_is_the_first_tab_you_can_see()
    {
        var vm = await Project();
        var workspace = vm.Workspace;
        var tabs = FourTabs(workspace);
        workspace.SetPinned(tabs[3], true);
        var navigator = new TabNavigator(() => new Keymap([]));

        navigator.SelectByIndex(workspace, 1);

        Assert.Same(tabs[3], workspace.SelectedTab);
    }

    // ---- persistence ----------------------------------------------------------------------------

    [Fact]
    public async Task Pinning_survives_a_restart()
    {
        // Pinning says which scripts matter, which is worth keeping across sessions — unlike a scroll
        // position or a zoom.
        var directory = Path.Combine(_root, nameof(Pinning_survives_a_restart));
        var first = await OpenAt(directory);
        var tabs = FourTabs(first.Workspace);
        await first.Workspace.SaveScriptAsync(tabs[1], Path.Combine(first.ScriptsDirectory!, "kept.sql"), "-- kept");
        first.Workspace.SetPinned(tabs[1], true);
        first.SaveWorkspace();

        var reopened = await OpenAt(directory);

        var restored = Assert.Single(reopened.Workspace.PinnedTabs);
        Assert.EndsWith("kept.sql", restored.ScriptPath);
        Assert.True(restored.IsPinned);
    }

    [Fact]
    public async Task An_older_session_restores_with_nothing_pinned()
    {
        // The field is new, so a session written before it says nothing — which must read as "not pinned"
        // rather than throwing or defaulting on.
        var vm = await Project();
        var session = new SessionState
        {
            OpenEditors = [new OpenEditor { ScratchText = "-- old" }],
            SelectedEditorIndex = 0,
        };

        await vm.Workspace.RestoreTabsAsync(session);

        Assert.Empty(vm.Workspace.PinnedTabs);
        Assert.Single(vm.Workspace.UnpinnedTabs);
    }

    private async Task<ShellViewModel> OpenAt(string directory)
    {
        var vm = new ShellViewModel(
            new ProviderRegistry(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            dialogs: new FakeDialogs(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        await vm.InitializeAsync(directory);
        return vm;
    }

    // ---- the view sync --------------------------------------------------------------------------

    [Fact]
    public void The_view_sync_patches_in_place_rather_than_clearing()
    {
        // Why it matters: these views back strips whose selection is bound two-way, and a Clear() makes the
        // control fix up its own selection and write that back — the shape of #87.
        var view = new ObservableCollection<string> { "a", "b", "c" };
        var events = 0;
        view.CollectionChanged += (_, _) => events++;

        TabViewSync.Apply(view, ["a", "b", "c"]);

        Assert.Equal(0, events);   // already correct: no edits at all
    }

    [Fact]
    public void The_view_sync_reaches_the_desired_list_from_any_starting_point()
    {
        var view = new ObservableCollection<string> { "c", "x", "a" };

        TabViewSync.Apply(view, ["a", "b", "c"]);

        Assert.Equal(["a", "b", "c"], view);
    }

    [Fact]
    public void The_view_sync_empties_a_view_whose_items_are_all_gone()
    {
        var view = new ObservableCollection<string> { "a", "b" };

        TabViewSync.Apply(view, []);

        Assert.Empty(view);
    }
}
