using System;
using System.Linq;
using Bearing.App.Controls;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Freezing and hiding columns (#118), and what that does to the selection and the cell cursor. Pure — the
/// grid only reads this state, so the rules that keep a user out of a corner are assertable without a
/// window (§2.5).
/// </summary>
public class ColumnLayoutTests
{
    private static ResultSetViewModel Result(int columns = 4, int rows = 3)
    {
        var descriptors = Enumerable.Range(0, columns)
            .Select(i => new ColumnDescriptor($"c{i}", "text", typeof(string)))
            .ToList();
        var data = Enumerable.Range(0, rows)
            .Select(r => Enumerable.Range(0, columns).Select(c => (object?)$"r{r}c{c}").ToArray())
            .ToList();
        return new ResultSetViewModel(
            new QueryResult(descriptors, data, data.Count, TimeSpan.Zero, null, null, false), "select 1", false);
    }

    // ---- hiding ---------------------------------------------------------------------------------------

    [Fact]
    public void Hiding_a_column_removes_it_from_the_visible_set_and_marks_the_meta_row()
    {
        var result = Result();
        var layout = result.ColumnLayout;

        Assert.True(layout.Hide(1));
        Assert.Equal([0, 2, 3], layout.VisibleColumns);
        Assert.Equal("1 column hidden", layout.HiddenText);
        Assert.Equal("1 column hidden", result.HiddenColumnsText);

        Assert.True(layout.Hide(2));
        Assert.Equal("2 columns hidden", layout.HiddenText);
    }

    [Fact]
    public void The_marker_is_null_when_nothing_is_hidden()
    {
        // Null rather than "0 columns hidden": the marker exists to say something is missing, and it must
        // not be on screen when nothing is.
        var result = Result();
        Assert.Null(result.ColumnLayout.HiddenText);
        Assert.Null(result.HiddenColumnsText);
    }

    [Fact]
    public void The_last_visible_column_cannot_be_hidden()
    {
        // A grid with no columns has no header left to right-click, so the action would be irreversible from
        // where the user is looking.
        var result = Result(columns: 2);
        var layout = result.ColumnLayout;

        Assert.True(layout.Hide(0));
        Assert.False(layout.Hide(1));
        Assert.Equal(1, layout.VisibleCount);
        Assert.Equal([1], layout.VisibleColumns);
    }

    [Fact]
    public void Hiding_the_same_column_twice_changes_nothing()
    {
        var layout = Result().ColumnLayout;
        Assert.True(layout.Hide(1));
        Assert.False(layout.Hide(1));
        Assert.Single(layout.Hidden);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void An_out_of_range_index_is_refused(int index)
        => Assert.False(Result(columns: 4).ColumnLayout.Hide(index));

    [Fact]
    public void Show_all_brings_every_column_back()
    {
        var layout = Result().ColumnLayout;
        layout.Hide(0);
        layout.Hide(2);

        Assert.True(layout.ShowAll());
        Assert.Equal([0, 1, 2, 3], layout.VisibleColumns);
        Assert.False(layout.ShowAll());   // nothing left to do
    }

    [Fact]
    public void The_layout_reports_every_change_once()
    {
        var layout = Result().ColumnLayout;
        var changes = 0;
        layout.Changed += () => changes++;

        layout.Hide(1);
        layout.Hide(1);      // refused
        layout.Freeze(2);
        layout.Freeze(2);    // already there
        layout.ShowAll();

        Assert.Equal(3, changes);
    }

    // ---- freezing -------------------------------------------------------------------------------------

    [Fact]
    public void Freezing_keeps_at_least_one_column_scrollable()
    {
        // A frozen pane covering the whole width leaves nothing to scroll — a state the mouse cannot undo.
        var layout = Result(columns: 4).ColumnLayout;

        Assert.True(layout.Freeze(4));
        Assert.Equal(3, layout.FrozenCount);

        Assert.False(layout.Freeze(99));  // already clamped to the same value
        Assert.Equal(3, layout.FrozenCount);
    }

    [Fact]
    public void Freezing_a_negative_count_is_no_freeze_rather_than_an_error()
    {
        var layout = Result().ColumnLayout;
        Assert.False(layout.Freeze(-3));
        Assert.Equal(0, layout.FrozenCount);
    }

    [Fact]
    public void Unfreeze_clears_it()
    {
        var layout = Result().ColumnLayout;
        layout.Freeze(2);
        Assert.True(layout.Unfreeze());
        Assert.Equal(0, layout.FrozenCount);
        Assert.False(layout.Unfreeze());
    }

    [Fact]
    public void Hiding_columns_narrows_an_existing_freeze_rather_than_leaving_it_illegal()
    {
        // Freeze three of four, then hide two: three frozen columns no longer exist to freeze, and a grid
        // asked to freeze more columns than it shows is the same unscrollable corner as above.
        var layout = Result(columns: 4).ColumnLayout;
        layout.Freeze(3);

        layout.Hide(0);
        layout.Hide(1);

        Assert.Equal(2, layout.VisibleCount);
        Assert.Equal(1, layout.FrozenCount);
    }

    // ---- what hiding does to the selection and the cursor ---------------------------------------------

    [Fact]
    public void A_hidden_column_is_not_part_of_select_all()
    {
        // Which is what makes Ctrl+A then Copy what-you-see. The exports are the path that deliberately
        // carries hidden columns (#118), and they read result.Columns rather than the selection.
        var result = Result(columns: 3, rows: 2);
        result.ColumnLayout.Hide(1);

        var cells = GridSelectionOps.AllCells(result);

        Assert.Equal(4, cells.Count);                        // 2 rows × 2 visible columns
        Assert.DoesNotContain(1, cells.Select(c => c.Col));
    }

    [Fact]
    public void A_hidden_column_inside_a_rectangle_is_skipped()
    {
        var result = Result(columns: 4, rows: 2);
        result.ColumnLayout.Hide(1);

        var cells = GridSelectionOps.Rectangle(result, (result.Rows[0], 0), (result.Rows[1], 3));

        Assert.Equal([0, 2, 3, 0, 2, 3], cells.Select(c => c.Col));
    }

    [Fact]
    public void A_whole_row_selection_covers_the_visible_columns_only()
    {
        var result = Result(columns: 3, rows: 2);
        result.ColumnLayout.Hide(0);

        var cells = GridSelectionOps.WholeRows(result, result.Rows[0], result.Rows[0]);

        Assert.Equal([1, 2], cells.Select(c => c.Col));
    }

    [Fact]
    public void The_cell_cursor_steps_over_a_hidden_column()
    {
        // Otherwise an arrow key parks the cursor on a cell with no visual, and the next keystroke edits
        // something the user cannot see.
        var result = Result(columns: 4);
        result.ColumnLayout.Hide(1);

        Assert.Equal(2, GridSelectionOps.StepColumn(result, 0, +1));
        Assert.Equal(0, GridSelectionOps.StepColumn(result, 2, -1));
    }

    [Fact]
    public void The_cursor_stops_at_the_edges_rather_than_landing_on_a_hidden_column()
    {
        var result = Result(columns: 3);
        result.ColumnLayout.Hide(0);
        result.ColumnLayout.Hide(2);

        Assert.Equal(1, GridSelectionOps.FirstColumn(result));
        Assert.Equal(1, GridSelectionOps.LastColumn(result));
        Assert.Equal(1, GridSelectionOps.StepColumn(result, 1, -1));
        Assert.Equal(1, GridSelectionOps.StepColumn(result, 1, +1));
    }

    [Fact]
    public void Home_and_end_land_on_visible_columns()
    {
        var result = Result(columns: 4);
        result.ColumnLayout.Hide(0);
        result.ColumnLayout.Hide(3);

        var home = GridSelectionOps.Move(result, 0, 2, GridMotion.Home, toEdge: false, pageSize: 10);
        var end = GridSelectionOps.Move(result, 0, 1, GridMotion.End, toEdge: false, pageSize: 10);

        Assert.Equal(1, home.Col);
        Assert.Equal(2, end.Col);
    }

    [Fact]
    public void Tab_across_a_row_boundary_lands_on_the_first_visible_column()
    {
        var result = Result(columns: 3, rows: 2);
        result.ColumnLayout.Hide(0);

        // From the last visible column of row 0, NextField wraps to row 1 — at column 1, not the hidden 0.
        var next = GridSelectionOps.Move(result, 0, 2, GridMotion.NextField, toEdge: false, pageSize: 10);

        Assert.Equal((1, 1), next);
    }

    // ---- regressions from the code review -------------------------------------------------------------

    /// <summary>
    /// A selection made <b>before</b> a hide must not survive it.
    /// <para>
    /// <see cref="GridSelectionOps"/> keeps a hidden column out of a <em>new</em> selection, which is what
    /// makes the invariant true by construction — but Ctrl+A then Hide left the hidden column's cells in the
    /// model, so Copy put the column the user had just hidden on the clipboard and Set NULL would have
    /// staged an UPDATE against a cell with no visual. Driven through the controller rather than a window:
    /// it is the only writer of the model, and none of this needs a grid.
    /// </para>
    /// </summary>
    [Fact]
    public void Hiding_a_column_drops_its_cells_from_a_selection_made_before_it()
    {
        var result = Result(columns: 4, rows: 3);
        var selection = new GridSelectionController(new Avalonia.Controls.Panel());
        selection.SelectAll(result);

        Assert.Equal(12, selection.Model.Cells.Count);          // 3 rows × 4 columns
        Assert.Contains(2, selection.Model.Cells.Select(c => c.Col));

        result.ColumnLayout.Hide(2);
        selection.DropHiddenColumns(result);

        Assert.DoesNotContain(2, selection.Model.Cells.Select(c => c.Col));
        Assert.Equal(9, selection.Model.Cells.Count);           // the rest survives
        // Copy reads straight from the model, so this is what would have reached the clipboard.
        Assert.DoesNotContain("c2", GridSelectionOps.Tsv(result, selection.Model.Cells));
    }

    [Fact]
    public void The_cursor_moves_off_a_column_that_is_hidden_under_it()
    {
        // Clearing would lose the row the user was on as well, so it moves rather than disappearing.
        var result = Result(columns: 4, rows: 3);
        var selection = new GridSelectionController(new Avalonia.Controls.Panel());
        selection.SelectAll(result);
        selection.Model.Active = (result.Rows[0], 0);
        selection.Model.Anchor = (result.Rows[0], 0);

        result.ColumnLayout.Hide(0);
        selection.DropHiddenColumns(result);

        Assert.NotNull(selection.Model.Active);
        Assert.False(result.ColumnLayout.IsHidden(selection.Model.Active!.Value.Col));
        Assert.False(result.ColumnLayout.IsHidden(selection.Model.Anchor!.Value.Col));
    }

    [Fact]
    public void Pruning_a_selection_that_belongs_to_another_result_does_nothing()
    {
        // The model holds one result's selection at a time; a layout change on a different result must not
        // reach into it.
        var shown = Result(columns: 3, rows: 2);
        var other = Result(columns: 3, rows: 2);
        var selection = new GridSelectionController(new Avalonia.Controls.Panel());
        selection.SelectAll(shown);
        var before = selection.Model.Cells.Count;

        other.ColumnLayout.Hide(1);
        selection.DropHiddenColumns(other);

        Assert.Equal(before, selection.Model.Cells.Count);
    }

    [Fact]
    public void The_nearest_visible_column_is_found_to_the_right_first_then_left()
    {
        // Where the cell cursor goes when the column it was on is hidden. Right first, because that is the
        // direction reading moves; left only at the edge; and never nowhere.
        var result = Result(columns: 4);
        result.ColumnLayout.Hide(1);
        Assert.Equal(2, GridSelectionOps.NearestVisibleColumn(result, 1));

        var atEnd = Result(columns: 4);
        atEnd.ColumnLayout.Hide(3);
        Assert.Equal(2, GridSelectionOps.NearestVisibleColumn(atEnd, 3));

        // A column that is not hidden is its own nearest.
        Assert.Equal(0, GridSelectionOps.NearestVisibleColumn(Result(), 0));
    }

    [Fact]
    public void Hiding_the_only_other_column_still_lands_the_cursor_somewhere_visible()
    {
        var result = Result(columns: 2);
        result.ColumnLayout.Hide(0);

        var landed = GridSelectionOps.NearestVisibleColumn(result, 0);
        Assert.False(result.ColumnLayout.IsHidden(landed));
    }

    [Fact]
    public void An_export_still_carries_every_column()
    {
        // The deliberate asymmetry: a selection is what you see, a file is the whole result. The meta row's
        // marker is what keeps that from being a surprise.
        var result = Result(columns: 3, rows: 2);
        result.ColumnLayout.Hide(1);

        var block = TableBlock.ForResult(result);

        Assert.Equal(3, block.Columns.Count);
        Assert.Equal(["c0", "c1", "c2"], block.Columns.Select(c => c.Name));
    }
}
