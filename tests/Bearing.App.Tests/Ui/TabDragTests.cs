using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Dragging a tab to a new position, as the gesture rather than as arithmetic.
/// <para>
/// <c>TabReorderTests</c> covers where a pointer lands and what that means in the master list; what it
/// cannot cover is whether a press on a realized tab header ever becomes a drag at all — the strip's tunnel
/// press handler, the pointer capture and the arranged container bounds are all in play, and every one of
/// them can leave the arithmetic correct and the gesture dead.
/// </para>
/// <para>
/// This is the reason the reorder is implemented with pointer capture rather than
/// <c>DragDrop.DoDragDropAsync</c>: a platform drag session runs its own modal loop and grabs the pointer,
/// so the gesture the trees use is not reachable from synthetic input (§4.5).
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class TabDragTests
{
    private readonly UiTestSession _ui;

    public TabDragTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task A_tab_dragged_past_its_neighbour_swaps_with_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_tab_dragged_past_its_neighbour_swaps_with_it));
        var workspace = shell.Vm.Workspace;
        var tabs = FourTabs(shell);

        // Tab 1 dragged over tab 2's right half: past the midpoint, so it lands after it.
        Drag(shell, from: 0, toRightHalfOf: 1);

        Assert.Equal([tabs[1], tabs[0], tabs[2], tabs[3]], workspace.Tabs);
    });

    [Fact]
    public Task A_tab_dragged_to_the_far_end_becomes_the_last_one() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_tab_dragged_to_the_far_end_becomes_the_last_one));
        var workspace = shell.Vm.Workspace;
        var tabs = FourTabs(shell);

        Drag(shell, from: 0, toRightHalfOf: 3);

        Assert.Equal([tabs[1], tabs[2], tabs[3], tabs[0]], workspace.Tabs);
    });

    [Fact]
    public Task A_tab_dragged_backwards_lands_before_the_tab_it_passed() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_tab_dragged_backwards_lands_before_the_tab_it_passed));
        var workspace = shell.Vm.Workspace;
        var tabs = FourTabs(shell);

        Drag(shell, from: 3, toLeftHalfOf: 1);

        Assert.Equal([tabs[0], tabs[3], tabs[1], tabs[2]], workspace.Tabs);
    });

    /// <summary>A press that goes nowhere is a click. Selecting a tab must not nudge it out of order because
    /// the hand moved a pixel on the way down.</summary>
    [Fact]
    public Task A_press_that_barely_moves_is_a_click_and_reorders_nothing() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_press_that_barely_moves_is_a_click_and_reorders_nothing));
        var workspace = shell.Vm.Workspace;
        var tabs = FourTabs(shell);
        var at = Label(shell, 0);

        shell.Window.MouseDown(at, MouseButton.Left);
        // LeftMouseButton on the move: that is how headless carries "the button is still down", and the
        // gesture reads exactly that to tell a drag from a hover.
        shell.Window.MouseMove(at + new Vector(2, 1), RawInputModifiers.LeftMouseButton);   // inside the 4px threshold
        shell.Window.MouseUp(at + new Vector(2, 1), MouseButton.Left);
        shell.Pump();

        Assert.Equal([tabs[0], tabs[1], tabs[2], tabs[3]], workspace.Tabs);
        Assert.False(tabs[0].IsDragging);
        Assert.Same(tabs[0], workspace.SelectedTab);   // it is still a click, and a click selects
    });

    /// <summary>
    /// The drag has to be visibly in flight, and the tab it is carrying has to be the marked one — the tabs
    /// do not move until the pointer is released, so <c>IsDragging</c> is the only thing distinguishing the
    /// tab being dragged from the tab the caret happens to be beside.
    /// </summary>
    [Fact]
    public Task The_dragged_tab_marks_itself_while_the_drag_is_in_flight() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_dragged_tab_marks_itself_while_the_drag_is_in_flight));
        var tabs = FourTabs(shell);
        var from = Label(shell, 0);
        var to = Label(shell, 2);

        shell.Window.MouseDown(from, MouseButton.Left);
        shell.Window.MouseMove(new Point(from.X + 20, from.Y), RawInputModifiers.LeftMouseButton);
        shell.Pump();

        Assert.True(tabs[0].IsDragging, "the press never became a drag");
        Assert.All(tabs.Skip(1), t => Assert.False(t.IsDragging));

        shell.Window.MouseUp(to, MouseButton.Left);
        shell.Pump();

        // …and it is cleared however the drag ends, or the tab stays dimmed for the rest of the session.
        Assert.All(tabs, t => Assert.False(t.IsDragging));
    });

    /// <summary>
    /// …and the dim is actually drawn. <c>IsDragging</c> reaching the header is a class binding plus a style
    /// selector on a template-generated panel, which is exactly the kind of wiring that resolves to nothing
    /// while every view-model assertion passes.
    /// </summary>
    [Fact]
    public Task The_dragged_header_is_dimmed_while_the_drag_is_in_flight() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_dragged_header_is_dimmed_while_the_drag_is_in_flight));
        var tabs = FourTabs(shell);
        var from = Label(shell, 0);

        var header = Header(shell, tabs[0]);
        var before = header.Opacity;

        shell.Window.MouseDown(from, MouseButton.Left);
        shell.Window.MouseMove(new Point(from.X + 20, from.Y), RawInputModifiers.LeftMouseButton);
        shell.Pump();

        Assert.True(tabs[0].IsDragging, "the press never became a drag");
        Assert.True(header.Opacity < before, $"the header was not dimmed (still {header.Opacity})");
        Assert.Equal(1, Header(shell, tabs[1]).Opacity);   // …and only that one

        shell.Window.MouseUp(new Point(from.X + 20, from.Y), MouseButton.Left);
        shell.Pump();

        Assert.Equal(before, Header(shell, tabs[0]).Opacity);
    });

    /// <summary>
    /// Dragging a tab up onto the pinned row pins it, at the position it was dropped. The rows are one
    /// gesture: the alternative is pinning with the toggle and then discovering the tab has appended itself
    /// to the other row wherever it liked.
    /// </summary>
    [Fact]
    public Task A_tab_dragged_onto_the_pinned_row_is_pinned_there() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_tab_dragged_onto_the_pinned_row_is_pinned_there));
        var workspace = shell.Vm.Workspace;
        var tabs = FourTabs(shell);
        // The pinned row is collapsed entirely when nothing is pinned, so it is not a drop target until
        // something is — which is what the pin toggle is for.
        workspace.SetPinned(tabs[0], true);
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        var start = Label(shell, IndexIn(shell, "TabStrip", tabs[3]));
        var pinned = Strip(shell, "PinnedTabStrip");
        var onto = pinned.ContainerFromIndex(0)!;
        var target = onto.TranslatePoint(new Point(onto.Bounds.Width * 0.8, onto.Bounds.Height / 2), shell.Window)!.Value;

        shell.Window.MouseDown(start, MouseButton.Left);
        shell.Window.MouseMove(new Point(start.X, start.Y - 6), RawInputModifiers.LeftMouseButton);
        shell.Window.MouseMove(target, RawInputModifiers.LeftMouseButton);
        shell.Window.MouseUp(target, MouseButton.Left);
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.True(tabs[3].IsDragging is false && tabs[3].IsPinned, "the tab did not end up pinned");
        Assert.Equal([tabs[0], tabs[3]], workspace.PinnedTabs);
    });

    /// <summary>
    /// The same gesture in the expanded strip, where the rows wrap: a drop on the second row has to land on
    /// the second row. Reading the pointer's X alone would file every one of them into the first.
    /// </summary>
    [Fact]
    public Task A_drop_on_the_second_row_of_an_expanded_strip_lands_there() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_drop_on_the_second_row_of_an_expanded_strip_lands_there));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var tabs = Enumerable.Range(1, 12).Select(i => workspace.NewTab($"-- {i}")).ToArray();
        foreach (var (tab, i) in tabs.Select((t, i) => (t, i))) tab.DisplayName = $"tab-{i + 1:00}";
        shell.Window.Width = 620;
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        shell.Window.ToggleTabStripExpanded();
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        var strip = Strip(shell, "TabStrip");
        var containers = Enumerable.Range(0, strip.ItemCount).Select(i => strip.ContainerFromIndex(i)!).ToArray();
        // The last row of the wrapped strip — as far from the first tab as this fixture goes, and reachable
        // only because the drop reads the pointer's row rather than its X.
        var onto = containers.OrderByDescending(c => c.Bounds.Y).First();
        Assert.True(onto.Bounds.Y > 0, "the fixture did not wrap, so there is no second row to drop on");

        var target = onto.TranslatePoint(new Point(onto.Bounds.Width * 0.2, onto.Bounds.Height / 2), shell.Window)!.Value;
        var landsBefore = (EditorTabViewModel)onto.DataContext!;
        var start = Label(shell, 0);

        shell.Window.MouseDown(start, MouseButton.Left);
        shell.Window.MouseMove(new Point(start.X + 8, start.Y), RawInputModifiers.LeftMouseButton);
        shell.Window.MouseMove(target, RawInputModifiers.LeftMouseButton);
        shell.Window.MouseUp(target, MouseButton.Left);
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        // Dropped on the left half of a tab on the last row, so it goes immediately before that tab — which
        // is most of the way down the list, not one place along.
        Assert.Equal(workspace.Tabs.IndexOf(landsBefore) - 1, workspace.Tabs.IndexOf(tabs[0]));
        Assert.True(workspace.Tabs.IndexOf(tabs[0]) > 5,
            $"the tab stayed near the top of the list (index {workspace.Tabs.IndexOf(tabs[0])})");
    });

    // ---- fixture ---------------------------------------------------------------------------------

    /// <summary>Four tabs with short, equal-ish labels, and a window wide enough that none overflows — the
    /// drag reads positions from arranged containers, so every tab has to be realized and on screen.</summary>
    private static EditorTabViewModel[] FourTabs(ShellHarness shell)
    {
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var tabs = Enumerable.Range(1, 4).Select(i => workspace.NewTab($"-- {i}")).ToArray();
        foreach (var (tab, i) in tabs.Select((t, i) => (t, i))) tab.DisplayName = $"tab-{i + 1}";
        workspace.SelectedTab = tabs[0];
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
        return tabs;
    }

    /// <summary>The panel the item template built for a tab — what the dim is applied to.</summary>
    private static StackPanel Header(ShellHarness shell, EditorTabViewModel tab)
        => shell.Window.GetVisualDescendants().OfType<StackPanel>()
            .First(p => ReferenceEquals(p.DataContext, tab) && p.Parent is ContentPresenter);

    private static TabStrip Strip(ShellHarness shell, string name = "TabStrip")
        => shell.Window.GetVisualDescendants().OfType<TabStrip>().First(s => s.Name == name);

    /// <summary>A tab's index in one of the strips — which is not its index in <c>Tabs</c> once a row has
    /// tabs of its own.</summary>
    private static int IndexIn(ShellHarness shell, string strip, EditorTabViewModel tab)
    {
        var items = Strip(shell, strip);
        for (var i = 0; i < items.ItemCount; i++)
            if (ReferenceEquals(items.ContainerFromIndex(i)?.DataContext, tab)) return i;
        Assert.Fail($"{tab.DisplayName} is not in {strip}");
        return -1;
    }

    /// <summary>
    /// Where to press a tab to drag it: over its label, in window coordinates.
    /// <para>
    /// Not the centre. The pin toggle and the ✕ sit in the right-hand third of a narrow tab and own their
    /// own presses (the pin toggles on the press itself), so the label is the drag handle — as it is in a
    /// browser, whose ✕ is not a drag handle either. A fixture that pressed the middle of an 99px tab landed
    /// on the pin and proved only that the pin still works.
    /// </para>
    /// </summary>
    private static Point Label(ShellHarness shell, int index) => At(shell, index, 0.2);

    private static Point At(ShellHarness shell, int index, double fraction)
    {
        var container = Strip(shell).ContainerFromIndex(index);
        Assert.NotNull(container);
        Assert.True(container!.Bounds.Width > 0, $"tab {index} is not arranged, so it has no position to press");
        var local = new Point(container.Bounds.Width * fraction, container.Bounds.Height / 2);
        var at = container.TranslatePoint(local, shell.Window);
        Assert.NotNull(at);
        return at!.Value;
    }

    /// <summary>
    /// Press a tab, travel to the target in a couple of steps, release. Two moves rather than one because
    /// the first is what crosses the threshold and starts the drag — a single jump would test a gesture that
    /// begins and ends in the same event.
    /// </summary>
    private static void Drag(ShellHarness shell, int from, int toRightHalfOf = -1, int toLeftHalfOf = -1)
    {
        var start = Label(shell, from);
        var target = toRightHalfOf >= 0 ? At(shell, toRightHalfOf, 0.8) : At(shell, toLeftHalfOf, 0.2);

        shell.Window.MouseDown(start, MouseButton.Left);
        shell.Window.MouseMove(new Point(start.X + (target.X > start.X ? 8 : -8), start.Y),
            RawInputModifiers.LeftMouseButton);
        shell.Window.MouseMove(target, RawInputModifiers.LeftMouseButton);
        shell.Window.MouseUp(target, MouseButton.Left);
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
    }
}
