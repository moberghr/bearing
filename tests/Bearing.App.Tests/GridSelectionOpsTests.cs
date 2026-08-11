using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The results grid's spreadsheet selection arithmetic — cursor motion, rectangle coverage, and the clipboard
/// shape. Previously welded into <c>ResultView</c>'s key handler and therefore unreachable by tests (Wayland
/// blocks headless keystrokes, §4.3); now pure over <see cref="GridSelectionOps"/>.
/// <para>
/// The recurring theme is the bool column: it renders as a checkbox that draws no selection ring, so the
/// cursor must never land on one and a rectangle must skip it.
/// </para>
/// </summary>
public class GridSelectionOpsTests
{
    /// <summary>(id int, name text, flag bool, qty int) — column 2 is the un-selectable checkbox.</summary>
    private static ResultSetViewModel Grid(params object?[][] rows)
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int)),
            new ColumnDescriptor("name", "text", typeof(string)),
            new ColumnDescriptor("flag", "bool", typeof(bool?)),
            new ColumnDescriptor("qty", "int4", typeof(int)),
        };
        var result = new QueryResult(columns, rows, rows.Length, TimeSpan.Zero, null, null, false);
        return new ResultSetViewModel(result, "select * from t", pageable: false);
    }

    private static ResultSetViewModel ThreeRows() => Grid(
        [1, "one", true, 10],
        [2, "two", false, 20],
        [3, "three", null, 30]);

    // ---- selectable columns ------------------------------------------------------------------

    [Fact]
    public void First_and_last_selectable_columns_skip_the_bool_column()
    {
        var rs = ThreeRows();
        Assert.Equal(0, GridSelectionOps.FirstSelectableColumn(rs));
        Assert.Equal(3, GridSelectionOps.LastSelectableColumn(rs)); // qty, not the bool at 2
    }

    [Fact]
    public void Stepping_right_jumps_over_the_bool_column()
    {
        var rs = ThreeRows();
        Assert.Equal(3, GridSelectionOps.StepColumn(rs, 1, +1)); // name -> qty, skipping flag
        Assert.Equal(1, GridSelectionOps.StepColumn(rs, 3, -1)); // qty -> name, skipping flag
    }

    [Fact]
    public void Stepping_past_an_edge_stays_put()
    {
        var rs = ThreeRows();
        Assert.Equal(0, GridSelectionOps.StepColumn(rs, 0, -1));
        Assert.Equal(3, GridSelectionOps.StepColumn(rs, 3, +1));
    }

    [Fact]
    public void An_all_bool_result_reports_column_zero_rather_than_minus_one()
    {
        // Degenerate but reachable (`select flag from t`): callers index Columns with the answer, so it must
        // stay in range even though nothing is really selectable.
        var single = new QueryResult(
            [new ColumnDescriptor("flag", "bool", typeof(bool))],
            new[] { new object?[] { true } }, 1, TimeSpan.Zero, null, null, false);
        var boolOnly = new ResultSetViewModel(single, "select flag from t", pageable: false);

        Assert.Equal(0, GridSelectionOps.FirstSelectableColumn(boolOnly));
        Assert.Equal(0, GridSelectionOps.LastSelectableColumn(boolOnly));
    }

    // ---- motion ------------------------------------------------------------------------------

    [Theory]
    [InlineData(GridMotion.Down, 1, 1)]      // steps one row
    [InlineData(GridMotion.Up, 0, 1)]        // clamps at the top
    [InlineData(GridMotion.PageDown, 2, 1)]  // page is larger than the result → clamps at the bottom
    public void Row_motion_clamps_within_the_loaded_rows(GridMotion motion, int expectedRow, int expectedCol)
    {
        var rs = ThreeRows();
        var (row, col) = GridSelectionOps.Move(rs, row: 0, col: 1, motion, toEdge: false, pageSize: 10);
        Assert.Equal(expectedRow, row);
        Assert.Equal(expectedCol, col);
    }

    [Fact]
    public void Ctrl_down_jumps_to_the_last_row_and_ctrl_up_to_the_first()
    {
        var rs = ThreeRows();
        Assert.Equal(2, GridSelectionOps.Move(rs, 0, 1, GridMotion.Down, toEdge: true, pageSize: 10).Row);
        Assert.Equal(0, GridSelectionOps.Move(rs, 2, 1, GridMotion.Up, toEdge: true, pageSize: 10).Row);
    }

    [Fact]
    public void Home_and_end_move_the_column_and_only_jump_rows_with_ctrl()
    {
        var rs = ThreeRows();

        var home = GridSelectionOps.Move(rs, 1, 3, GridMotion.Home, toEdge: false, pageSize: 10);
        Assert.Equal((1, 0), home);                                  // same row, first column

        var ctrlHome = GridSelectionOps.Move(rs, 1, 3, GridMotion.Home, toEdge: true, pageSize: 10);
        Assert.Equal((0, 0), ctrlHome);                              // top-left

        var ctrlEnd = GridSelectionOps.Move(rs, 1, 0, GridMotion.End, toEdge: true, pageSize: 10);
        Assert.Equal((2, 3), ctrlEnd);                               // bottom-right (skipping the bool)
    }

    [Fact]
    public void A_page_of_one_still_advances()
    {
        var rs = ThreeRows();
        // pageSize is derived from realized row visuals, which can measure as 0 rows mid-layout; a
        // PageDown that moved nowhere would look like a dead key.
        Assert.Equal(1, GridSelectionOps.Move(rs, 0, 0, GridMotion.PageDown, toEdge: false, pageSize: 0).Row);
    }

    // ---- rectangles --------------------------------------------------------------------------

    [Fact]
    public void A_rectangle_covers_every_selectable_cell_between_its_corners()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.Rectangle(rs, (rs.Rows[0], 1), (rs.Rows[1], 3));

        // rows 0-1 × columns 1,3 (the bool at 2 is skipped) = 4 cells
        Assert.Equal(4, cells.Count);
        Assert.DoesNotContain(2, cells.Select(c => c.Col));
        Assert.Contains((rs.Rows[0], 1), cells);
        Assert.Contains((rs.Rows[1], 3), cells);
    }

    [Fact]
    public void A_rectangle_is_corner_order_independent()
    {
        var rs = ThreeRows();
        var forward = GridSelectionOps.Rectangle(rs, (rs.Rows[0], 0), (rs.Rows[2], 1));
        var reversed = GridSelectionOps.Rectangle(rs, (rs.Rows[2], 1), (rs.Rows[0], 0));
        Assert.Equal(forward.ToHashSet(), reversed.ToHashSet());
    }

    [Fact]
    public void A_rectangle_anchored_on_a_dropped_row_is_empty()
    {
        var rs = ThreeRows();
        // Discarding a pending-new row removes it from Rows while a selection may still name it.
        var stranded = new object?[] { 99, "gone", null, 0 };
        Assert.Empty(GridSelectionOps.Rectangle(rs, (stranded, 0), (rs.Rows[0], 1)));
    }

    [Fact]
    public void All_cells_excludes_bool_columns()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.AllCells(rs);
        Assert.Equal(9, cells.Count); // 3 rows × 3 selectable columns
        Assert.DoesNotContain(2, cells.Select(c => c.Col));
    }

    // ---- clipboard ---------------------------------------------------------------------------

    [Fact]
    public void Tsv_emits_the_selected_rows_and_columns_as_a_table()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.Rectangle(rs, (rs.Rows[0], 0), (rs.Rows[1], 1));

        Assert.Equal("1\tone\n2\ttwo", GridSelectionOps.Tsv(rs, cells));
    }

    [Fact]
    public void Tsv_keeps_the_shape_of_a_non_rectangular_selection_by_blanking_gaps()
    {
        var rs = ThreeRows();
        // (row0,col0) and (row1,col1) only — the two off-diagonal slots have no selected cell.
        var cells = new List<(object?[] Row, int Col)> { (rs.Rows[0], 0), (rs.Rows[1], 1) };

        Assert.Equal("1\t\n\ttwo", GridSelectionOps.Tsv(rs, cells));
    }

    [Fact]
    public void Tsv_of_nothing_is_empty()
        => Assert.Equal("", GridSelectionOps.Tsv(ThreeRows(), Array.Empty<(object?[], int)>()));

    [Fact]
    public void Tsv_orders_by_row_and_column_index_not_by_selection_order()
    {
        var rs = ThreeRows();
        var clickedBackwards = new List<(object?[] Row, int Col)>
        {
            (rs.Rows[1], 1), (rs.Rows[1], 0), (rs.Rows[0], 1), (rs.Rows[0], 0),
        };
        Assert.Equal("1\tone\n2\ttwo", GridSelectionOps.Tsv(rs, clickedBackwards));
    }

    // ---- stats feed --------------------------------------------------------------------------

    [Fact]
    public void Measure_values_drop_key_columns_so_stats_never_sum_identifiers()
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int)),
            new ColumnDescriptor("owner_id", "int4", typeof(int)),
            new ColumnDescriptor("qty", "int4", typeof(int)),
        };
        var result = new QueryResult(columns, new[] { new object?[] { 7, 8, 9 } }, 1, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select * from t", pageable: false)
        {
            PrimaryKeyColumns = [0],
            ForeignKeyColumns = [1],
        };

        var values = GridSelectionOps.MeasureValues(rs, GridSelectionOps.AllCells(rs)).ToList();

        Assert.Equal(new object?[] { 9 }, values); // qty only — not the PK, not the FK
    }
}
