using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// #183: pressing a grid's scroll bar is scrolling, not a click-away. A press the cells and headers didn't
/// take cleared the selection, and the scroll bar was one of those — so dragging the thumb to look along a
/// highlighted row threw the highlight away. Empty space below the last row still clears.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ScrollBarSelectionTests
{
    private readonly UiTestSession _ui;

    public ScrollBarSelectionTests(UiTestSession ui) => _ui = ui;

    [Theory]
    [InlineData(Orientation.Horizontal)]
    [InlineData(Orientation.Vertical)]
    public Task Dragging_a_scroll_bar_keeps_the_selection(Orientation orientation) => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 24);
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        Press(window, ResultsHarness.RequireCell(view, rows[2], 1));
        Assert.Contains((rows[2], 1), view.Selection.Model.Cells);

        var bar = grid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == orientation);
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().First(t => t.IsEffectivelyVisible && t.Bounds.Width > 0);
        var from = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
        var step = orientation == Orientation.Horizontal ? new Point(30, 0) : new Point(0, 30);
        var before = bar.Value;

        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        ResultsHarness.Pump(window);
        Assert.Contains((rows[2], 1), view.Selection.Model.Cells);   // the press alone used to clear it
        for (var i = 1; i <= 4; i++)
        {
            window.MouseMove(from + step * i, RawInputModifiers.LeftMouseButton);
            ResultsHarness.Pump(window);
        }
        window.MouseUp(from + step * 4, MouseButton.Left);
        ResultsHarness.Pump(window);

        Assert.True(bar.Value > before, $"the drag must have scrolled, value {before} → {bar.Value}");
        Assert.Contains((rows[2], 1), view.Selection.Model.Cells);
        window.Close();
    });

    /// <summary>The click-away that the scroll bar must not trigger still works where it belongs.</summary>
    [Fact]
    public Task Clicking_empty_space_below_the_rows_still_clears_the_selection() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 3);
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        Press(window, ResultsHarness.RequireCell(view, rows[1], 1));
        Assert.NotEmpty(view.Selection.Model.Cells);

        var empty = grid.TranslatePoint(new Point(grid.Bounds.Width / 3, grid.Bounds.Height - 40), window)!.Value;
        Press(window, empty);
        Assert.Empty(view.Selection.Model.Cells);
        window.Close();
    });

    private static void Press(Window window, Visual target)
        => Press(window, target.TranslatePoint(new Point(8, target.Bounds.Height / 2), window)
                         ?? throw new InvalidOperationException("target is not connected to the window"));

    private static void Press(Window window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        ResultsHarness.Pump(window);
    }
}
