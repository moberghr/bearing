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
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// What moves the results viewport, and what must not (#60). Both facts here were measured on the headless
/// harness rather than reasoned about, and both were surprises:
/// <list type="bullet">
/// <item>Focusing the grid resets <i>both</i> scroll offsets to zero even with the clicked cell already handed
/// to it as <c>SelectedItem</c> + <c>CurrentColumn</c> — the comment on <c>ResultCellFactory.FocusClickedCell</c>
/// claims that makes it a no-op, and it does not. The viewport survives a click only because
/// <c>KeepClickedCellInView</c> scrolls back afterwards, so that corrective is load-bearing, not belt-and-braces.</item>
/// <item>Committing an edit does <i>not</i> throw the viewport away, contrary to #60's first two suspects. The
/// first pending edit does cost one pixel — the commit group appearing makes the meta row a pixel taller — but
/// that is a pixel, not the jump the issue describes.</item>
/// </list>
/// So these are guards on behavior that is currently correct, not a fix. #60's reported jump has not been
/// reproduced from a driven edit; if it returns, the reporter's exact gesture is the missing input.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ResultGridScrollTests
{
    private readonly UiTestSession _ui;

    public ResultGridScrollTests(UiTestSession ui) => _ui = ui;

    /// <summary>A click on a cell while scrolled right and down leaves the viewport where it was. This is the
    /// regression guard for <c>KeepClickedCellInView</c>: delete that corrective and both offsets go to 0.</summary>
    [Fact]
    public Task Clicking_a_cell_while_scrolled_keeps_the_viewport_still() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        var target = rows[35];
        grid.ScrollIntoView(target, grid.Columns[10]);
        ResultsHarness.Pump(window);

        var (beforeH, beforeV) = Offsets(grid);
        Assert.True(beforeH > 0 && beforeV > 0, $"fixture must scroll both ways, got h={beforeH} v={beforeV}");
        var before = ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 10), grid);

        Click(window, ResultsHarness.RequireCell(view, target, 10));
        ResultsHarness.Pump(window);

        Assert.Equal((beforeH, beforeV), Offsets(grid));
        Assert.Equal(before, ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 10), grid));
        window.Close();
    });

    /// <summary>Committing an inline edit leaves the edited cell where it was. The one pixel of tolerance is
    /// the meta row growing as the Discard/Save group appears on the first pending edit — measured, and the
    /// reason #60 suggests reserving that group's height.</summary>
    [Fact]
    public Task Committing_an_edit_leaves_the_edited_cell_in_place() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        var target = rows[35];
        grid.ScrollIntoView(target, grid.Columns[10]);
        ResultsHarness.Pump(window);
        Click(window, ResultsHarness.RequireCell(view, target, 10));
        ResultsHarness.Pump(window);

        var (beforeH, _) = Offsets(grid);
        var before = ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 10), grid);

        Assert.True(grid.BeginEdit(), "the grid refused to begin editing");
        ResultsHarness.Pump(window);
        var editor = grid.GetVisualDescendants().OfType<TextBox>().SingleOrDefault();
        Assert.NotNull(editor);
        Assert.Equal("r36c10-value", editor.Text);
        editor.Text = "edited";
        Assert.True(grid.CommitEdit(), "the grid refused to commit");
        ResultsHarness.Pump(window);

        // The edit landed on the result set, pending rather than written.
        Assert.Equal("edited", target[10]);
        Assert.True(rs.HasPendingChanges);

        // …and the cell it landed on is still realized, in exactly the same place. No tolerance any more:
        // the one pixel that used to be here was the commit group appearing and re-measuring the grid, and
        // the meta row now reserves its height (ResultChrome.MetaRowContentHeight).
        var after = ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 10), grid);
        Assert.Equal(beforeH, Offsets(grid).Horizontal);
        Assert.Equal(before, after);
        window.Close();
    });

    /// <summary>The first pending edit does not change the grid's height. The commit group
    /// (● N pending · Discard · Save) is a pixel taller than the row's other buttons, so revealing it grew
    /// the meta row and re-measured the grid beneath it — which is the reflow #60 asks to be reserved
    /// away.</summary>
    [Fact]
    public Task The_first_pending_edit_does_not_reflow_the_grid() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        Assert.False(rs.HasPendingChanges);
        var before = grid.Bounds.Height;

        rs.SetCell(rows[0], 1, "edited");
        ResultsHarness.Pump(window);

        Assert.True(rs.HasPendingChanges, "the fixture must have made the commit group appear");
        Assert.Equal(before, grid.Bounds.Height);
        window.Close();
    });

    /// <summary>…and neither does discarding them again, so the reserve holds in both directions.</summary>
    [Fact]
    public Task Clearing_the_pending_edits_does_not_reflow_the_grid_either() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        rs.SetCell(rows[0], 1, "edited");
        ResultsHarness.Pump(window);
        var dirty = grid.Bounds.Height;

        rs.ClearPending();
        ResultsHarness.Pump(window);

        Assert.False(rs.HasPendingChanges);
        Assert.Equal(dirty, grid.Bounds.Height);
        window.Close();
    });

    /// <summary>The grid scrolls itself rather than sitting in a ScrollViewer, so its offsets are the
    /// scrollbars' values.</summary>
    private static (double Horizontal, double Vertical) Offsets(DataGrid grid)
    {
        var bars = grid.GetVisualDescendants().OfType<ScrollBar>().ToList();
        return (bars.First(b => b.Orientation == Orientation.Horizontal).Value,
                bars.First(b => b.Orientation == Orientation.Vertical).Value);
    }

    /// <summary>A real left click in the middle of a realized cell, through the windowing platform — so the
    /// cell's own PointerPressed handler runs, correctives and all.</summary>
    private static void Click(Window window, Border cell)
    {
        var point = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("cell is not connected to the window");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }
}
