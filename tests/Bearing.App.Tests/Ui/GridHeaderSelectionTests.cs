using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Selecting from the grid's headers with the mouse. The corner above the row-number gutter selects the
/// whole result (#55) — it is a <c>DataGridColumnHeader</c> in the template with no column behind it, so it
/// used to fall through to "chrome" and <i>clear</i> the selection, which is the opposite of what every
/// spreadsheet does with that square.
/// </summary>
[Collection(UiTestCollection.Name)]
public class GridHeaderSelectionTests
{
    private readonly UiTestSession _ui;

    public GridHeaderSelectionTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Clicking_the_corner_selects_the_whole_result() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 4, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);

        Assert.Empty(view.Selection.Model.Cells);
        ClickCorner(window, view);

        Assert.Equal(GridSelectionOps.AllCells(rs).Count, view.Selection.Model.Cells.Count);
        Assert.Same(rs, view.Selection.Model.Result);
        window.Close();
    });

    /// <summary>It is the same operation Ctrl+A runs, not a second implementation that could drift from it.
    /// Asserted on the cells themselves, so a difference in which cells are selectable would show.</summary>
    [Fact]
    public Task The_corner_selects_exactly_what_select_all_does() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 4, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);

        ClickCorner(window, view);
        var fromCorner = view.Selection.Model.Cells.ToHashSet();

        view.Selection.ClearAndNotify();
        view.Selection.SelectAll(rs);

        Assert.Equal(view.Selection.Model.Cells.ToHashSet(), fromCorner);
        window.Close();
    });

    /// <summary>An empty result has nothing to select, and must not leave the grid claiming it has.</summary>
    [Fact]
    public Task The_corner_on_an_empty_result_selects_nothing() => _ui.Run(() =>
    {
        var rs = ResultsHarness.SingleColumn("id", "int4", typeof(int), primaryKey: true);
        var (window, view) = ResultsHarness.Show(rs);

        ClickCorner(window, view);

        Assert.Empty(view.Selection.Model.Cells);
        window.Close();
    });

    /// <summary>The corner no longer clears, but everything else that is not a cell or a header still does —
    /// that click-away is what the None case exists for.</summary>
    [Fact]
    public Task A_click_on_the_grids_empty_space_still_clears() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 4, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);

        ClickCorner(window, view);
        Assert.NotEmpty(view.Selection.Model.Cells);

        // Well below the last row: grid chrome, not a cell.
        var grid = ResultsHarness.Grid(view);
        var below = grid.TranslatePoint(new Point(grid.Bounds.Width / 2, grid.Bounds.Height - 8), window)
                    ?? throw new InvalidOperationException("grid is not in the window");
        window.MouseMove(below);
        window.MouseDown(below, MouseButton.Left);
        window.MouseUp(below, MouseButton.Left);
        ResultsHarness.Pump(window);

        Assert.Empty(view.Selection.Model.Cells);
        window.Close();
    });

    private static void ClickCorner(Window window, Visual view)
    {
        var corner = view.GetVisualDescendants()
            .OfType<Control>()
            .First(c => c.Name == "PART_TopLeftCornerHeader");
        var point = corner.TranslatePoint(new Point(corner.Bounds.Width / 2, corner.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("the corner is not in the window");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.UpdateLayout();
    }
}
