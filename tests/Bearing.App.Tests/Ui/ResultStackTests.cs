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

    // ---- the weights, on their own --------------------------------------------------------------

    [Fact]
    public void A_tiny_set_still_gets_the_floor()
        => Assert.Equal(ResultStackWeights.Min, ResultStackWeights.For(Set(1)));

    [Fact]
    public void A_huge_set_stops_at_the_ceiling()
    {
        // Past the ceiling the extra rows would only starve the neighbours: both sets scroll internally
        // anyway, so neither becomes more readable.
        Assert.Equal(ResultStackWeights.Max, ResultStackWeights.For(Set(5_000)));
        Assert.Equal(ResultStackWeights.For(Set(5_000)), ResultStackWeights.For(Set(50_000)));
    }

    [Fact]
    public void In_between_the_weight_is_the_row_count()
        => Assert.Equal(12, ResultStackWeights.For(Set(12)));

    [Fact]
    public void A_non_grid_result_gets_the_floor()
    {
        // A statement message or an error has no rows to be proportional to.
        var message = new QueryResult([], [], 0, TimeSpan.Zero, "UPDATE 3", null, true);
        var vm = new Bearing.App.ViewModels.ResultSetViewModel(message, null, pageable: false);
        Assert.Equal(ResultStackWeights.Min, ResultStackWeights.For(vm));
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>The per-set containers, in order: the direct children of the stack's grid that are not
    /// dividers.</summary>
    private static Control[] SetContainers(Visual view)
    {
        var grid = Stack(view).GetVisualDescendants().OfType<Grid>().First();
        return grid.Children.OfType<Control>().Where(c => c is not GridSplitter).ToArray();
    }

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
