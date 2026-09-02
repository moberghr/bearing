using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.Results;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Trading space between stacked result sets (#81). Every set used to be clamped to a flat 360px, so a
/// three-row set and a nine-hundred-row set were the same height and the only way to favour one was to
/// collapse the others.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ResultStackTests
{
    private readonly UiTestSession _ui;

    public ResultStackTests(UiTestSession ui) => _ui = ui;

    /// <summary>A read-only set of <paramref name="rows"/> single-column rows.</summary>
    private static Bearing.App.ViewModels.ResultSetViewModel Set(int rows)
    {
        var data = Enumerable.Range(1, rows).Select(i => new object?[] { i }).ToList();
        var result = new QueryResult(
            [new ColumnDescriptor("id", "int4", typeof(int))], data, data.Count, TimeSpan.Zero, null, null, false);
        return new Bearing.App.ViewModels.ResultSetViewModel(result, "select id from t", pageable: false);
    }

    private static ResultStackView Stack(Visual view)
        => view.GetVisualDescendants().OfType<ResultStackView>().Single();

    [Fact]
    public Task Adjacent_sets_get_a_divider_between_them() => _ui.Run(() =>
    {
        var (window, view) = ResultsHarness.Show(Set(3), Set(50), Set(9));

        // One between each pair, and none above the first or below the last.
        Assert.Equal(2, Stack(view).Dividers.Count);
        Assert.All(Stack(view).Dividers, d => Assert.Equal(GridResizeDirection.Rows, d.ResizeDirection));
        window.Close();
    });

    [Fact]
    public Task A_divider_says_it_can_be_dragged() => _ui.Run(() =>
    {
        // The mechanism worked from the start; what it lacked was any sign of it. Avalonia's GridSplitter
        // sets no resize cursor of its own, and a 4px bar in the same brush as every static rule in the app
        // is indistinguishable from a decoration.
        var (window, view) = ResultsHarness.Show(Set(20), Set(20));

        var divider = Stack(view).Dividers.Single();
        Assert.IsType<PaneDivider>(divider);
        // A plain GridSplitter leaves Cursor null — Avalonia's control sets none — so this is the assertion
        // that fails if the line setting it is ever removed. Avalonia's Cursor exposes no way to read the
        // StandardCursorType back, so "it sets one at all" is what can be checked here.
        Assert.NotNull(divider.Cursor);
        // …and the base control genuinely provides none, which is what made the seam read as decoration.
        Assert.Null(new GridSplitter { ResizeDirection = GridResizeDirection.Rows }.Cursor);
        // Grabbable: the row it occupies is taller than the seam it draws, and it is hit-testable.
        Assert.True(divider.Bounds.Height >= PaneDivider.GrabThickness,
            $"the divider is only {divider.Bounds.Height}px tall");
        Assert.NotNull(divider.Background);
        window.Close();
    });

    [Fact]
    public Task The_divider_changes_colour_under_the_pointer_and_under_a_drag() => _ui.Run(() =>
    {
        // The gap this closes: the affordance was verified by looking at captures, and the suite only checked
        // that a cursor was set. Colour is assertable after all — from the rendered pixels, which is the one
        // way to check a Render override without re-implementing it.
        var (window, view) = ResultsHarness.Show(Set(30), Set(30));
        var divider = Stack(view).Dividers.Single();

        var rest = SeamPixel(window, divider);

        var centre = divider.TranslatePoint(
            new Point(divider.Bounds.Width / 2, divider.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre);
        ResultsHarness.Pump(window);
        var hovered = SeamPixel(window, divider);

        window.MouseDown(centre, MouseButton.Left);
        window.MouseMove(centre + new Vector(0, 4));
        ResultsHarness.Pump(window);
        var dragged = SeamPixel(window, divider);
        window.MouseUp(centre + new Vector(0, 4), MouseButton.Left);

        // Three distinct states, and both live ones brighter than the resting rule. Distinctness and
        // brightness only — FrameCapture's contract is that channel order is not guaranteed, so "it is teal"
        // is not something a test here may claim (the eye check for that is the capture in LookProbe).
        Assert.NotEqual(rest, hovered);
        Assert.NotEqual(hovered, dragged);
        Assert.NotEqual(rest, dragged);
        Assert.True(FrameCapture.Brightness(hovered) > FrameCapture.Brightness(rest),
            "hover is not brighter than the resting seam");
        Assert.True(FrameCapture.Brightness(dragged) > FrameCapture.Brightness(rest),
            "the drag state is not brighter than the resting seam");
        window.Close();
    });

    /// <summary>The pixel the divider actually paints at its own centre line, away from the grip.</summary>
    private static uint SeamPixel(Window window, Control divider)
    {
        var at = divider.TranslatePoint(new Point(40, divider.Bounds.Height / 2), window)!.Value;
        return FrameCapture.Of(window).At((int)at.X, (int)at.Y);
    }

    [Fact]
    public Task The_editor_results_seam_is_the_same_divider() => _ui.Run(async () =>
    {
        // The old comment claimed the two read as the same affordance while they were separately written and
        // had drifted. Now they are the same class.
        using var shell = await ShellHarness.ShowAsync(nameof(The_editor_results_seam_is_the_same_divider));

        var seams = shell.Window.GetVisualDescendants().OfType<PaneDivider>().ToList();
        Assert.NotEmpty(seams);
        Assert.All(seams, s => Assert.Equal(GridResizeDirection.Rows, s.ResizeDirection));
    });

    [Fact]
    public Task A_single_set_has_no_divider_and_no_stack() => _ui.Run(() =>
    {
        // One result takes the uncapped path; there is nothing to trade space with.
        var (window, view) = ResultsHarness.Show(Set(20));

        Assert.Empty(view.GetVisualDescendants().OfType<ResultStackView>());
        window.Close();
    });

    [Fact]
    public Task A_bigger_set_opens_taller_than_a_smaller_one() => _ui.Run(() =>
    {
        // The flat 360px cap is what this replaces: the run should open roughly in proportion to what each
        // set returned.
        var small = Set(3);
        var large = Set(200);
        var (window, view) = ResultsHarness.Show(small, large);
        var stack = Stack(view);

        Assert.True(stack.WeightOf(large) > stack.WeightOf(small),
            $"small={stack.WeightOf(small)} large={stack.WeightOf(large)}");
        window.Close();
    });

    [Fact]
    public Task Collapsing_a_set_gives_its_space_up() => _ui.Run(() =>
    {
        // A collapsed set holding a star row open would free nothing, which is the one thing the chevron is
        // for. Driven through the chevron, so the wiring from ResultView is covered too.
        var first = Set(10);
        var second = Set(10);
        var (window, view) = ResultsHarness.Show(first, second);
        var stack = Stack(view);
        Assert.NotNull(stack.WeightOf(first));

        ClickChevron(window, view, ofSet: 0);

        Assert.Null(stack.WeightOf(first));          // no longer a star row
        Assert.NotNull(stack.WeightOf(second));      // the neighbour keeps its share
        window.Close();
    });

    [Fact]
    public Task Reopening_a_set_takes_its_share_back() => _ui.Run(() =>
    {
        var first = Set(10);
        var (window, view) = ResultsHarness.Show(first, Set(10));
        var stack = Stack(view);
        var opened = stack.WeightOf(first);

        ClickChevron(window, view, ofSet: 0);
        Assert.Null(stack.WeightOf(first));
        ClickChevron(window, view, ofSet: 0);

        Assert.Equal(opened, stack.WeightOf(first));
        window.Close();
    });

    [Fact]
    public Task Dragging_a_divider_trades_height_between_the_sets() => _ui.Run(() =>
    {
        var (window, view) = ResultsHarness.Show(Set(20), Set(20));
        var divider = Stack(view).Dividers.Single();
        var containers = SetContainers(view);
        var before = containers.Select(c => c.Bounds.Height).ToList();

        Drag(window, divider, dy: 90);

        var after = containers.Select(c => c.Bounds.Height).ToList();
        Assert.True(after[0] > before[0], $"the first set did not grow: {before[0]} -> {after[0]}");
        Assert.True(after[1] < before[1], $"the second set did not give way: {before[1]} -> {after[1]}");
        // The pane is the same size, so what one gained the other lost — to within the pixel the divider's
        // own placement rounds away.
        Assert.True(Math.Abs(before.Sum() - after.Sum()) <= 2,
            $"the sets' total height changed: {before.Sum()} -> {after.Sum()}");
        window.Close();
    });

    [Fact]
    public Task A_divider_cannot_erase_a_set() => _ui.Run(() =>
    {
        // Dragging a set to nothing is a way to lose a result you meant to keep, so there is a floor.
        var (window, view) = ResultsHarness.Show(Set(20), Set(20));
        var divider = Stack(view).Dividers.Single();

        Drag(window, divider, dy: 5000);

        var second = SetContainers(view)[1];
        Assert.True(second.Bounds.Height > 0, "the second set was erased");
        window.Close();
    });

    // ---- nothing is squeezed out ----------------------------------------------------------------

    [Fact]
    public Task The_smallest_set_still_shows_its_rows() => _ui.Run(() =>
    {
        // The first cut sized the floor under the chrome, so a three-row set beside a big one rendered as a
        // meta bar and a column header with none of its three rows below (#81).
        var small = Set(3);
        var (window, view) = ResultsHarness.Show(small, Set(240), Set(12));

        var rows = VisibleRows(SetContainers(view)[0]);
        Assert.True(rows >= 1, $"the 3-row set showed {rows} of its rows");
        window.Close();
    });

    [Fact]
    public Task No_set_is_pushed_off_the_bottom_of_the_pane() => _ui.Run(() =>
    {
        // A Grid clamps a star row to its MinHeight and then arranges the rest as if it had not, so an
        // extreme weight ratio overflowed the pane and the last set was simply not on screen.
        var (window, view) = ResultsHarness.Show(Set(3), Set(240), Set(12));
        var stack = Stack(view);

        AssertFits(stack, SetContainers(view));
        window.Close();
    });

    [Fact]
    public Task Sets_share_a_pane_too_short_for_all_their_floors() => _ui.Run(() =>
    {
        // Nine sets cannot all be legible in a short pane. Equally cramped and all present beats comfortable
        // and truncated, so the floor yields rather than overflowing.
        var sets = Enumerable.Range(0, 9).Select(_ => Set(30)).ToArray();
        var (window, view) = ResultsHarness.Show(sets);
        var stack = Stack(view);

        var containers = SetContainers(view);
        Assert.All(containers, c => Assert.True(c.Bounds.Height > 0, "a set was squeezed to nothing"));
        AssertFits(stack, containers);
        window.Close();
    });

    [Fact]
    public Task A_message_takes_only_the_room_it_needs() => _ui.Run(() =>
    {
        // A star row would hold a share of the pane open for one line of text, and a message has nothing to
        // scroll — so it gets an Auto row and the grids divide the rest.
        var message = new Bearing.App.ViewModels.ResultSetViewModel(
            new QueryResult([], [], 3, TimeSpan.Zero, "UPDATE 3", null, true), null, pageable: false);
        var grid = Set(60);
        var (window, view) = ResultsHarness.Show(grid, message);
        var stack = Stack(view);

        Assert.Null(stack.WeightOf(message));         // not a star row
        Assert.NotNull(stack.WeightOf(grid));
        var containers = SetContainers(view);
        Assert.True(containers[1].Bounds.Height < containers[0].Bounds.Height / 3,
            $"the message took {containers[1].Bounds.Height}px of a {stack.Bounds.Height}px pane");
        Assert.True(containers[1].Bounds.Height > 0, "the message is not visible at all");
        window.Close();
    });

    [Fact]
    public Task Collapsing_a_message_and_reopening_it_leaves_it_as_it_was() => _ui.Run(() =>
    {
        // Reopening must not promote a message to a star row it never had.
        var message = new Bearing.App.ViewModels.ResultSetViewModel(
            new QueryResult([], [], 3, TimeSpan.Zero, "UPDATE 3", null, true), null, pageable: false);
        var (window, view) = ResultsHarness.Show(Set(20), message);
        var stack = Stack(view);

        stack.SetCollapsed(message, true);
        stack.SetCollapsed(message, false);

        Assert.Null(stack.WeightOf(message));
        window.Close();
    });

    // ---- the weights, on their own --------------------------------------------------------------

    [Fact]
    public void An_empty_set_gets_the_floor()
        => Assert.Equal(ResultStackWeights.Min, ResultStackWeights.For(Set(0)));

    [Fact]
    public void A_result_with_no_grid_gets_the_floor()
    {
        // A statement message or an error has no rows to be proportional to.
        var message = new QueryResult([], [], 0, TimeSpan.Zero, "UPDATE 3", null, true);
        var vm = new Bearing.App.ViewModels.ResultSetViewModel(message, null, pageable: false);
        Assert.Equal(ResultStackWeights.Min, ResultStackWeights.For(vm));
    }

    [Fact]
    public void A_huge_set_stops_at_the_ceiling()
    {
        Assert.Equal(ResultStackWeights.Max, ResultStackWeights.For(Set(ResultStackWeights.Cap)));
        Assert.Equal(ResultStackWeights.Max, ResultStackWeights.For(Set(50_000)));
    }

    [Fact]
    public void More_rows_is_always_taller()
    {
        var weights = new[] { 1, 5, 20, 80, 199 }.Select(rows => ResultStackWeights.For(Set(rows))).ToList();
        Assert.Equal(weights.OrderBy(w => w), weights);
        Assert.Equal(weights.Distinct(), weights);
    }

    [Fact]
    public void Ten_times_the_rows_is_taller_but_nothing_like_ten_times_taller()
    {
        // The point of the log scale: the ordering survives, the 60:1 share that pinned a small set at its
        // floor — and overflowed the pane — does not.
        var few = ResultStackWeights.For(Set(3));
        var many = ResultStackWeights.For(Set(180));

        Assert.True(many > few, $"{many} !> {few}");
        Assert.True(many / few < 3, $"the ratio is still extreme: {many}/{few}");
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>The per-set containers, in order: the direct children of the stack's grid that are not
    /// dividers.</summary>
    private static Control[] SetContainers(Visual view)
    {
        var grid = Stack(view).GetVisualDescendants().OfType<Grid>().First();
        return grid.Children.OfType<Control>().Where(c => c is not GridSplitter).ToArray();
    }

    /// <summary>
    /// Assert the stack ends inside its pane, so no set is pushed off the bottom.
    /// <para>
    /// The tolerance is a pixel per set, not zero: a <see cref="Grid"/> snaps each star row up to a whole
    /// pixel, so equal rows can add up to a pixel more than the pane per row. The bug this guards against
    /// clipped whole sets — an extreme weight ratio clamped a row and the rest were arranged as if it had not.
    /// </para>
    /// </summary>
    private static void AssertFits(Control stack, Control[] containers)
    {
        var last = containers[^1];
        var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), stack)!.Value.Y;
        Assert.True(bottom <= stack.Bounds.Height + containers.Length,
            $"{containers.Length} sets end at {bottom} in a {stack.Bounds.Height} pane");
    }

    /// <summary>How many of a set's data rows are actually drawn inside its container.</summary>
    private static int VisibleRows(Control container)
        => container.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.Bounds.Height > 0 && r.IsVisible);

    private static void ClickChevron(Window window, Visual view, int ofSet)
    {
        // The chevron's padded hit target: a 16x16 transparent border holding the triangle (ResultChrome).
        var chevron = SetContainers(view)[ofSet]
            .GetVisualDescendants()
            .OfType<Border>()
            .First(b => b is { Width: 16, Height: 16 });
        Press(window, chevron, new Point(8, 8));
        ResultsHarness.Pump(window);
    }

    private static void Drag(Window window, Control handle, double dy)
    {
        var from = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)
                   ?? throw new InvalidOperationException("the divider is not in the window");
        var to = from + new Vector(0, dy);
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        ResultsHarness.Pump(window);
    }

    private static void Press(Window window, Control target, Point at)
    {
        var point = target.TranslatePoint(at, window)
                    ?? throw new InvalidOperationException("the control is not in the window");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }
}
