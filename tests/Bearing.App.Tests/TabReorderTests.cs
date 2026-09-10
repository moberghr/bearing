using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
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
/// Dragging tabs into a new order: which gap the pointer is in, and what that means in the master tab list
/// behind the two rows.
/// <para>
/// The second half is the one worth pinning. The strips render <c>PinnedTabs</c> / <c>UnpinnedTabs</c>, two
/// views over one <c>Tabs</c> list (§9.1), so "third in the unpinned row" is not an index into anything —
/// and getting the translation wrong does not throw, it silently files the tab somewhere else.
/// </para>
/// </summary>
public class TabReorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-reorder", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    // ---- which gap the pointer is in ------------------------------------------------------------

    /// <summary>Four 100px tabs on one row, as a strip arranges them.</summary>
    private static Rect[] OneRow() =>
    [
        new Rect(0, 0, 100, 28), new Rect(100, 0, 100, 28),
        new Rect(200, 0, 100, 28), new Rect(300, 0, 100, 28),
    ];

    [Fact]
    public void The_left_half_of_a_tab_drops_before_it()
    {
        var gap = TabReorder.Hit(OneRow(), new Point(210, 14));

        Assert.Equal(2, gap.Slot);
        Assert.Equal(200, gap.Caret.X);       // the caret sits on that tab's leading edge
        Assert.Equal(28, gap.Caret.Height);
    }

    [Fact]
    public void The_right_half_drops_after_it()
    {
        var gap = TabReorder.Hit(OneRow(), new Point(290, 14));

        Assert.Equal(3, gap.Slot);
        Assert.Equal(300, gap.Caret.X);
    }

    [Fact]
    public void Past_the_last_tab_is_the_end_of_the_row()
    {
        var gap = TabReorder.Hit(OneRow(), new Point(900, 14));

        Assert.Equal(4, gap.Slot);
        Assert.Equal(400, gap.Caret.X);
    }

    [Fact]
    public void Before_the_first_tab_is_the_start()
    {
        var gap = TabReorder.Hit(OneRow(), new Point(-40, 14));

        Assert.Equal(0, gap.Slot);
        Assert.Equal(0, gap.Caret.X);
    }

    /// <summary>
    /// The expanded strip wraps, so the second row repeats the first row's X range. Reading X alone would
    /// put every drop on row two into row one — the pointer's Y is what picks the row.
    /// </summary>
    [Fact]
    public void A_wrapped_strip_drops_on_the_row_the_pointer_is_level_with()
    {
        Rect[] wrapped =
        [
            new Rect(0, 0, 100, 28), new Rect(100, 0, 100, 28),      // row one
            new Rect(0, 28, 100, 28), new Rect(100, 28, 100, 28),    // row two
        ];

        // Same X, different rows: the first tab of row one, and the first tab of row two.
        Assert.Equal(0, TabReorder.Hit(wrapped, new Point(10, 14)).Slot);
        var second = TabReorder.Hit(wrapped, new Point(10, 40));
        Assert.Equal(2, second.Slot);
        Assert.Equal(28, second.Caret.Y);   // …and the caret is drawn on that row, not the first
    }

    /// <summary>Past the last tab of a wrapped row lands there, not at the end of the whole list.</summary>
    [Fact]
    public void Past_the_end_of_a_wrapped_row_stays_on_that_row()
    {
        Rect[] wrapped =
        [
            new Rect(0, 0, 100, 28), new Rect(100, 0, 100, 28),
            new Rect(0, 28, 100, 28),
        ];

        Assert.Equal(2, TabReorder.Hit(wrapped, new Point(900, 14)).Slot);
    }

    [Fact]
    public void A_pointer_below_every_row_falls_back_to_the_end()
    {
        // A drag that strays out of a 28px strip still means what it plainly means; refusing would make the
        // gesture feel broken every time the hand wobbled.
        Assert.Equal(4, TabReorder.Hit(OneRow(), new Point(150, 400)).Slot);
    }

    [Fact]
    public void An_unrealized_container_has_no_position_and_is_skipped()
    {
        Rect[] partly = [new Rect(0, 0, 100, 28), default, new Rect(100, 0, 100, 28)];

        var gap = TabReorder.Hit(partly, new Point(190, 14));

        Assert.Equal(3, gap.Slot);
        Assert.Equal(200, gap.Caret.X);
    }

    [Fact]
    public void An_empty_strip_has_one_slot_and_nothing_to_draw()
    {
        var gap = TabReorder.Hit([], new Point(10, 10));

        Assert.Equal(0, gap.Slot);
        Assert.Equal(0, gap.Caret.Height);   // no caret: MoveTo refuses a zero-height gap
    }

    /// <summary>The dragged tab is still in the strip while the drag is in flight, so every gap to its right
    /// is one too far once it is lifted out.</summary>
    [Fact]
    public void The_dragged_tab_is_taken_out_of_the_count()
    {
        Assert.Equal(2, TabReorder.SlotWithoutDragged(slot: 3, draggedIndex: 1));
        Assert.Equal(1, TabReorder.SlotWithoutDragged(slot: 1, draggedIndex: 1));   // its own left edge
        Assert.Equal(0, TabReorder.SlotWithoutDragged(slot: 0, draggedIndex: 3));
        // Dragged in from the other row: it is not in this strip's count at all.
        Assert.Equal(3, TabReorder.SlotWithoutDragged(slot: 3, draggedIndex: -1));
    }

    // ---- and where that lands in the master list ------------------------------------------------

    private static bool[] Unpinned(int n) => Enumerable.Repeat(false, n).ToArray();

    [Fact]
    public void A_slot_in_a_single_row_strip_is_the_master_index()
    {
        // Move index 0 to the end: with it removed the other three sit at 0,1,2, so it goes to 3.
        Assert.Equal(3, TabReorder.TargetIndex(Unpinned(4), from: 0, targetPinned: false, slot: 3));
        Assert.Equal(0, TabReorder.TargetIndex(Unpinned(4), from: 3, targetPinned: false, slot: 0));
        Assert.Equal(1, TabReorder.TargetIndex(Unpinned(4), from: 3, targetPinned: false, slot: 1));
    }

    [Fact]
    public void A_slot_past_the_row_is_clamped_to_its_end()
    {
        Assert.Equal(3, TabReorder.TargetIndex(Unpinned(4), from: 1, targetPinned: false, slot: 99));
    }

    /// <summary>
    /// The interesting one: the target row's tabs are scattered through the master list, so the slot has to
    /// be translated through the pinned flags rather than used as an index.
    /// </summary>
    [Fact]
    public void A_slot_in_one_row_skips_past_the_other_rows_tabs()
    {
        //  index: 0      1        2      3        4
        //         pinned unpinned pinned unpinned unpinned
        bool[] mixed = [true, false, true, false, false];

        // Dragging the last tab (4, unpinned) to the front of the unpinned row. Its row is master 1 and 3,
        // which sit at 1 and 3 with it removed — so the front of the row is 1, not 0.
        Assert.Equal(1, TabReorder.TargetIndex(mixed, from: 4, targetPinned: false, slot: 0));
        // …and to the front of the *pinned* row, which pins it: master 0 and 2.
        Assert.Equal(0, TabReorder.TargetIndex(mixed, from: 4, targetPinned: true, slot: 0));
        Assert.Equal(2, TabReorder.TargetIndex(mixed, from: 4, targetPinned: true, slot: 1));
        Assert.Equal(3, TabReorder.TargetIndex(mixed, from: 4, targetPinned: true, slot: 2));
    }

    [Fact]
    public void Pinning_the_first_tab_of_an_empty_pinned_row_puts_it_at_the_front()
    {
        // Nothing to pick a position between, and the pinned row leads the list.
        Assert.Equal(0, TabReorder.TargetIndex(Unpinned(3), from: 2, targetPinned: true, slot: 0));
    }

    [Fact]
    public void Unpinning_the_last_tab_of_an_empty_unpinned_row_puts_it_at_the_end()
    {
        bool[] allPinned = [true, true, true];

        Assert.Equal(2, TabReorder.TargetIndex(allPinned, from: 0, targetPinned: false, slot: 0));
    }

    [Fact]
    public void A_tab_that_is_not_in_the_list_has_no_destination()
    {
        Assert.Equal(-1, TabReorder.TargetIndex(Unpinned(3), from: -1, targetPinned: false, slot: 0));
        Assert.Equal(-1, TabReorder.TargetIndex(Unpinned(3), from: 7, targetPinned: false, slot: 0));
    }

    // ---- the move itself, through the workspace -------------------------------------------------

    private Task<ShellViewModel> Project([System.Runtime.CompilerServices.CallerMemberName] string name = "")
        => OpenAt(Path.Combine(_root, name));

    private static EditorTabViewModel[] FourTabs(WorkspaceViewModel workspace)
    {
        workspace.Tabs.Clear();
        return Enumerable.Range(1, 4).Select(i => workspace.NewTab($"-- {i}")).ToArray();
    }

    /// <summary>Assert the master order by identity. Not by header: <c>Tabs.Clear()</c> does not reset the
    /// scratch counter, so the labels a fixture gets depend on what the project opened with.</summary>
    private static void AssertOrder(WorkspaceViewModel workspace, params EditorTabViewModel[] expected)
        => Assert.Equal(expected, workspace.Tabs);

    [Fact]
    public async Task A_tab_dragged_to_the_front_leads_the_strip()
    {
        var vm = await Project();
        var tabs = FourTabs(vm.Workspace);

        Assert.True(vm.Workspace.MoveTab(tabs[2], pinned: false, slot: 0));

        AssertOrder(vm.Workspace, tabs[2], tabs[0], tabs[1], tabs[3]);
        // The rows are views over the master list, so they follow without being asked.
        Assert.Equal(vm.Workspace.Tabs, vm.Workspace.UnpinnedTabs);
    }

    [Fact]
    public async Task A_tab_dragged_to_the_end_trails_it()
    {
        var vm = await Project();
        var tabs = FourTabs(vm.Workspace);

        Assert.True(vm.Workspace.MoveTab(tabs[0], pinned: false, slot: 3));

        AssertOrder(vm.Workspace, tabs[1], tabs[2], tabs[3], tabs[0]);
    }

    /// <summary>A drag that ends where it started must not report a move — the strip would rebuild its rows
    /// for nothing, and the session would be marked as changed by a click.</summary>
    [Fact]
    public async Task A_drag_that_ends_where_it_started_moves_nothing()
    {
        var vm = await Project();
        var tabs = FourTabs(vm.Workspace);

        Assert.False(vm.Workspace.MoveTab(tabs[1], pinned: false, slot: 1));
        AssertOrder(vm.Workspace, tabs[0], tabs[1], tabs[2], tabs[3]);
    }

    [Fact]
    public async Task Reordering_does_not_change_which_tab_is_selected()
    {
        var vm = await Project();
        var tabs = FourTabs(vm.Workspace);
        vm.Workspace.SelectedTab = tabs[1];

        vm.Workspace.MoveTab(tabs[3], pinned: false, slot: 0);

        Assert.Same(tabs[1], vm.Workspace.SelectedTab);
    }

    /// <summary>Dropping a tab on the other row pins it — the same gesture browsers use, and it needs a
    /// position in that row rather than being appended to it.</summary>
    [Fact]
    public async Task A_tab_dropped_on_the_pinned_row_is_pinned_at_that_position()
    {
        var vm = await Project();
        var tabs = FourTabs(vm.Workspace);
        vm.Workspace.SetPinned(tabs[0], true);
        vm.Workspace.SetPinned(tabs[1], true);

        Assert.True(vm.Workspace.MoveTab(tabs[3], pinned: true, slot: 1));

        Assert.True(tabs[3].IsPinned);
        Assert.Equal([tabs[0], tabs[3], tabs[1]], vm.Workspace.PinnedTabs);
        Assert.Equal([tabs[2]], vm.Workspace.UnpinnedTabs);
    }

    [Fact]
    public async Task A_pinned_tab_dropped_on_the_strip_is_unpinned()
    {
        var vm = await Project();
        var tabs = FourTabs(vm.Workspace);
        vm.Workspace.SetPinned(tabs[0], true);

        Assert.True(vm.Workspace.MoveTab(tabs[0], pinned: false, slot: 0));

        Assert.False(tabs[0].IsPinned);
        Assert.Empty(vm.Workspace.PinnedTabs);
        Assert.False(vm.Workspace.HasPinnedTabs);   // the row hides again
        Assert.Equal([tabs[0], tabs[1], tabs[2], tabs[3]], vm.Workspace.UnpinnedTabs);
    }

    /// <summary>
    /// A cross-row drop that changes only the flag still has to re-split the rows: the master order it needs
    /// is the order it already had, so there is no <c>Move</c> to raise the collection change that normally
    /// does it.
    /// </summary>
    [Fact]
    public async Task Repinning_without_moving_still_re_splits_the_rows()
    {
        var vm = await Project();
        var tabs = FourTabs(vm.Workspace);

        // Tab 1 is already first in master order, which is where a pinned tab belongs.
        Assert.True(vm.Workspace.MoveTab(tabs[0], pinned: true, slot: 0));

        Assert.Equal(0, vm.Workspace.Tabs.IndexOf(tabs[0]));
        Assert.Equal([tabs[0]], vm.Workspace.PinnedTabs);
        Assert.True(vm.Workspace.HasPinnedTabs);
    }

    /// <summary>The order the strip shows is the order the session saves, so a rearranged strip comes back
    /// rearranged — most of the point of being able to rearrange it.</summary>
    [Fact]
    public async Task A_reordered_strip_survives_a_restart()
    {
        var directory = Path.Combine(_root, nameof(A_reordered_strip_survives_a_restart));
        var first = await OpenAt(directory);
        var tabs = FourTabs(first.Workspace);
        for (var i = 0; i < tabs.Length; i++)
            await first.Workspace.SaveScriptAsync(
                tabs[i], Path.Combine(first.ScriptsDirectory!, $"q{i + 1}.sql"), $"-- {i + 1}");
        first.Workspace.MoveTab(tabs[3], pinned: false, slot: 0);
        first.SaveWorkspace();

        var reopened = await OpenAt(directory);

        Assert.Equal(["q4.sql", "q1.sql", "q2.sql", "q3.sql"],
            reopened.Workspace.Tabs.Select(t => t.Header).ToArray());
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
}
