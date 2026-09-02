using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The results grid's spreadsheet selection arithmetic — cursor motion, rectangle coverage, and the clipboard
/// shape. Previously welded into <c>ResultView</c>'s key handler and therefore unreachable by tests; now pure
/// over <see cref="GridSelectionOps"/>, which is still the preferred shape even though headless UI tests
/// exist (§4.3) — this runs in milliseconds and in parallel.
/// <para>
/// The bool column used to be the recurring theme here, skipped by every operation because its checkbox drew
/// no selection ring. It now carries the same ring as any other cell (#9), so these assert the opposite: the
/// cursor lands on it, rectangles cover it, and Ctrl+A takes it.
/// </para>
/// </summary>
public class GridSelectionOpsTests
{
    /// <summary>(id int, name text, flag bool, qty int) — column 2 is the checkbox column. Read-only.</summary>
    private static ResultSetViewModel Grid(params object?[][] rows) => Build(null, rows);

    /// <summary>The same four columns, editable, with <c>id</c> declared NOT NULL — the nullability a Set NULL
    /// has to respect.</summary>
    private static ResultSetViewModel Editable(params object?[][] rows) => Build(
        new EditTarget("public", "t",
        [
            new EditableColumn(0, "id", IsPrimaryKey: true, NotNull: true),
            new EditableColumn(1, "name", IsPrimaryKey: false),
            new EditableColumn(2, "flag", IsPrimaryKey: false),
            new EditableColumn(3, "qty", IsPrimaryKey: false),
        ]), rows);

    private static ResultSetViewModel Build(EditTarget? target, object?[][] rows)
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int)),
            new ColumnDescriptor("name", "text", typeof(string)),
            new ColumnDescriptor("flag", "bool", typeof(bool?)),
            new ColumnDescriptor("qty", "int4", typeof(int)),
        };
        var result = new QueryResult(columns, rows, rows.Length, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select * from t", pageable: false) { EditTarget = target };
        if (target is not null) rs.CaptureOriginals();
        return rs;
    }

    private static ResultSetViewModel ThreeRows() => Grid(
        [1, "one", true, 10],
        [2, "two", false, 20],
        [3, "three", null, 30]);

    // ---- columns -----------------------------------------------------------------------------

    [Fact]
    public void First_and_last_columns_span_the_whole_result()
    {
        var rs = ThreeRows();
        Assert.Equal(0, GridSelectionOps.FirstColumn(rs));
        Assert.Equal(3, GridSelectionOps.LastColumn(rs));
    }

    [Fact]
    public void Stepping_lands_on_the_checkbox_column_instead_of_jumping_it()
    {
        var rs = ThreeRows();
        Assert.Equal(2, GridSelectionOps.StepColumn(rs, 1, +1)); // name -> flag
        Assert.Equal(2, GridSelectionOps.StepColumn(rs, 3, -1)); // qty  -> flag
    }

    [Fact]
    public void Stepping_past_an_edge_stays_put()
    {
        var rs = ThreeRows();
        Assert.Equal(0, GridSelectionOps.StepColumn(rs, 0, -1));
        Assert.Equal(3, GridSelectionOps.StepColumn(rs, 3, +1));
    }

    [Fact]
    public void A_checkbox_only_result_is_still_selectable()
    {
        // `select flag from t` used to have nothing selectable at all — the cursor had nowhere to go and
        // Ctrl+C copied nothing.
        var single = new QueryResult(
            [new ColumnDescriptor("flag", "bool", typeof(bool))],
            new[] { new object?[] { true } }, 1, TimeSpan.Zero, null, null, false);
        var boolOnly = new ResultSetViewModel(single, "select flag from t", pageable: false);

        Assert.Equal(0, GridSelectionOps.FirstColumn(boolOnly));
        Assert.Equal(0, GridSelectionOps.LastColumn(boolOnly));
        Assert.Single(GridSelectionOps.AllCells(boolOnly));
        Assert.Equal("True", GridSelectionOps.Tsv(boolOnly, GridSelectionOps.AllCells(boolOnly)));
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
        Assert.Equal((2, 3), ctrlEnd);                               // bottom-right
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
    public void A_rectangle_covers_every_cell_between_its_corners()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.Rectangle(rs, (rs.Rows[0], 1), (rs.Rows[1], 3));

        // rows 0-1 × columns 1,2,3 — the checkbox at 2 included
        Assert.Equal(6, cells.Count);
        Assert.Contains((rs.Rows[0], 2), cells);
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
    public void All_cells_includes_the_checkbox_column()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.AllCells(rs);
        Assert.Equal(12, cells.Count); // 3 rows × 4 columns
        Assert.Contains(2, cells.Select(c => c.Col));
    }

    // ---- whole rows / columns (header clicks, #6) ----------------------------------------------

    [Fact]
    public void A_row_header_click_takes_the_whole_width_of_the_row()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.WholeRows(rs, rs.Rows[1], rs.Rows[1]);

        Assert.Equal(4, cells.Count); // every column, checkbox included
        Assert.All(cells, c => Assert.Same(rs.Rows[1], c.Row));
        Assert.Equal([0, 1, 2, 3], cells.Select(c => c.Col).OrderBy(c => c).ToArray());
    }

    [Fact]
    public void Shift_clicking_a_second_row_header_takes_the_contiguous_rows()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.WholeRows(rs, rs.Rows[0], rs.Rows[2]);

        Assert.Equal(12, cells.Count); // 3 rows × 4 columns
        Assert.Equal(3, cells.Select(c => c.Row).Distinct().Count());
    }

    [Fact]
    public void A_column_header_click_takes_every_loaded_row_of_that_column()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.WholeColumns(rs, 1, 1);

        Assert.Equal(3, cells.Count);
        Assert.All(cells, c => Assert.Equal(1, c.Col));
    }

    [Fact]
    public void Shift_clicking_a_second_column_header_takes_the_contiguous_columns()
    {
        var rs = ThreeRows();
        var cells = GridSelectionOps.WholeColumns(rs, 1, 3);

        Assert.Equal(9, cells.Count); // 3 rows × columns 1..3
        Assert.Equal([1, 2, 3], cells.Select(c => c.Col).Distinct().OrderBy(c => c).ToArray());
    }

    [Fact]
    public void A_column_selection_stops_at_the_loaded_rows_of_a_paged_result()
    {
        // The honest half of the answer to "what does a column mean on a paged result": it covers what is
        // loaded, and the grid says so. Silently spanning 3 of 1,000 rows is how a Copy as ▸ IN list comes
        // out short without anyone noticing.
        var rs = ThreeRows();
        rs.HasMore = true;
        rs.TotalCount = 1000;

        Assert.Equal(3, GridSelectionOps.WholeColumns(rs, 0, 0).Count);
        // The count is deliberately localized — "1.000" is right on hr-HR — so the culture is pinned rather
        // than the separator assumed. What is under test is the phrase: the loaded count, then the total.
        CultureScope.In("en-US", () => Assert.Equal("3 of 1,000 rows", rs.RowCountText));
    }

    // ---- field traversal (#10) -----------------------------------------------------------------

    private static (int Row, int Col) Field(ResultSetViewModel rs, int row, int col, bool forward = true)
        => GridSelectionOps.Move(
            rs, row, col, forward ? GridMotion.NextField : GridMotion.PreviousField, toEdge: false, pageSize: 10);

    [Fact]
    public void Tab_walks_the_fields_of_a_row()
    {
        var rs = ThreeRows();
        Assert.Equal((1, 1), Field(rs, 1, 0));
        Assert.Equal((1, 2), Field(rs, 1, 1));
        Assert.Equal((1, 3), Field(rs, 1, 2));
    }

    [Fact]
    public void Tab_off_the_last_field_starts_the_next_row()
        // The whole point of a field traversal rather than a Right arrow: it does not stop at the edge, it
        // continues in reading order.
        => Assert.Equal((2, 0), Field(ThreeRows(), 1, 3));

    [Fact]
    public void Shift_tab_off_the_first_field_ends_the_previous_row()
        => Assert.Equal((0, 3), Field(ThreeRows(), 1, 0, forward: false));

    [Fact]
    public void Shift_tab_walks_back_along_a_row()
        => Assert.Equal((1, 1), Field(ThreeRows(), 1, 2, forward: false));

    [Fact]
    public void Tab_stops_at_the_last_field_of_the_result()
    {
        // Stopping rather than cycling: past the last field is the end of the data, and wrapping to the top
        // would throw the cursor a screen away from where the user is looking.
        var rs = ThreeRows();
        Assert.Equal((2, 3), Field(rs, 2, 3));
        Assert.Equal((0, 0), Field(rs, 0, 0, forward: false));
    }

    [Fact]
    public void A_field_traversal_covers_a_bool_column_like_any_other()
        // Column 2 is the checkbox column; it takes the cursor exactly as the rest do (#9).
        => Assert.Equal((0, 2), Field(ThreeRows(), 0, 1));

    [Fact]
    public void A_single_column_result_tabs_straight_down_the_rows()
    {
        // Every field is also the row's last, so a traversal is pure row motion — the degenerate case that
        // would loop forever or stick if the wrap were written as "move right, then fix up".
        var one = new QueryResult(
            [new ColumnDescriptor("id", "int4", typeof(int))],
            new[] { new object?[] { 1 }, new object?[] { 2 }, new object?[] { 3 } },
            3, TimeSpan.Zero, null, null, false);
        var single = new ResultSetViewModel(one, "select id from t", pageable: false);

        Assert.Equal((1, 0), Field(single, 0, 0));
        Assert.Equal((2, 0), Field(single, 1, 0));
        Assert.Equal((0, 0), Field(single, 1, 0, forward: false));
    }

    [Fact]
    public void Field_motions_are_the_ones_that_ignore_shift_as_an_extend()
    {
        Assert.True(GridSelectionOps.IsFieldMotion(GridMotion.NextField));
        Assert.True(GridSelectionOps.IsFieldMotion(GridMotion.PreviousField));
        Assert.False(GridSelectionOps.IsFieldMotion(GridMotion.Right));
        Assert.False(GridSelectionOps.IsFieldMotion(GridMotion.Down));
    }

    [Fact]
    public void A_header_click_on_an_empty_result_selects_nothing()
    {
        var empty = Grid();
        Assert.Empty(GridSelectionOps.WholeColumns(empty, 0, 0));
        Assert.Empty(GridSelectionOps.WholeRows(empty, new object?[] { 1, "x", null, 2 }, new object?[] { 1, "x", null, 2 }));
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

    // ---- Set NULL ----------------------------------------------------------------------------

    [Fact]
    public void Set_null_takes_every_selected_cell_that_may_hold_null_including_the_checkbox_column()
    {
        var rs = Editable([1, "one", true, 10]);
        var row = rs.Rows[0];

        var plan = GridSelectionOps.PlanSetNull(rs, new[] { (row, 1), (row, 2), (row, 3) });

        Assert.Equal(new[] { (row, 1), (row, 2), (row, 3) }, plan.Targets);
        Assert.Equal(0, plan.NotNullable);
    }

    [Fact]
    public void Set_null_refuses_a_not_null_column_and_says_how_many()
    {
        var rs = Editable([1, "one", true, 10]);
        var row = rs.Rows[0];

        // id is NOT NULL: writing it would stage an UPDATE the server is certain to reject, so it drops out
        // and is counted — the caller reports it rather than letting a partial write pass for a whole one.
        var plan = GridSelectionOps.PlanSetNull(rs, new[] { (row, 0), (row, 1) });

        Assert.Equal(new[] { (row, 1) }, plan.Targets);
        Assert.Equal(1, plan.NotNullable);
    }

    [Fact]
    public void Set_null_skips_cells_that_already_mean_null_without_counting_them_as_refused()
    {
        var rs = Editable([1, null, null, 10]);
        var row = rs.Rows[0];
        row[3] = CellFormat.NullToken;   // a previous Set NULL / paste left the token in the buffer

        var plan = GridSelectionOps.PlanSetNull(rs, new[] { (row, 1), (row, 2), (row, 3) });

        Assert.Empty(plan.Targets);      // nothing to write — and so no row marked edited for nothing
        Assert.Equal(0, plan.NotNullable);
    }

    [Fact]
    public void Set_null_targets_come_back_in_row_then_column_order()
    {
        var rs = Editable([1, "one", true, 10], [2, "two", false, 20]);

        // Fed in the order a Ctrl-click sweep produced (the model holds them in a hash set).
        var plan = GridSelectionOps.PlanSetNull(rs, new[]
        {
            (rs.Rows[1], 3), (rs.Rows[0], 3), (rs.Rows[1], 1), (rs.Rows[0], 1),
        });

        Assert.Equal(
            new[] { (rs.Rows[0], 1), (rs.Rows[0], 3), (rs.Rows[1], 1), (rs.Rows[1], 3) },
            plan.Targets);
    }

    [Fact]
    public void Set_null_ignores_a_column_index_off_the_end_of_the_result()
    {
        var rs = Editable([1, "one", true, 10]);
        var row = rs.Rows[0];

        var plan = GridSelectionOps.PlanSetNull(rs, new[] { (row, 1), (row, 9) });

        Assert.Equal(new[] { (row, 1) }, plan.Targets);
        Assert.Equal(0, plan.NotNullable);   // out of range is not a nullability refusal
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
