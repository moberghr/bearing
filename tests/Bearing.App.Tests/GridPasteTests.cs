using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The paste shape rules (#8): what the clipboard's text means, and which cells a paste therefore writes.
/// This is the whole reason <see cref="GridPaste"/> is pure — a paste that silently writes one cell too far
/// right, or quietly drops the bottom of a block, is invisible in a screenshot and Wayland blocks driving the
/// grid headlessly (§4.3).
/// </summary>
public class GridPasteTests
{
    private static readonly EditTarget Target = new("public", "t",
    [
        new EditableColumn(0, "id", IsPrimaryKey: true),
        new EditableColumn(1, "name", IsPrimaryKey: false),
        new EditableColumn(2, "qty", IsPrimaryKey: false),
    ]);

    /// <summary>(id int, name text, qty int) — three rows, originals captured.</summary>
    private static ResultSetViewModel ThreeRows()
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), 1, 1),
            new ColumnDescriptor("name", "text", typeof(string), 1, 2),
            new ColumnDescriptor("qty", "int4", typeof(int), 1, 3),
        };
        object?[][] rows = [[1, "one", 10], [2, "two", 20], [3, "three", 30]];
        var result = new QueryResult(columns, rows, rows.Length, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select * from t", pageable: false) { EditTarget = Target };
        rs.CaptureOriginals();
        return rs;
    }

    private static List<(object?[] Row, int Col)> Cells(ResultSetViewModel rs, params (int Row, int Col)[] cells)
        => cells.Select(c => (rs.Rows[c.Row], c.Col)).ToList();

    // ---- parsing -----------------------------------------------------------------------------

    [Fact]
    public void A_bare_value_parses_as_a_one_by_one_block()
    {
        var block = GridPaste.Parse("hello");
        Assert.Equal(new[] { new[] { "hello" } }, block.Select(r => r.ToArray()));
    }

    [Fact]
    public void Tabs_split_columns_and_newlines_split_rows()
    {
        var block = GridPaste.Parse("a\tb\nc\td");
        Assert.Equal(2, block.Count);
        Assert.Equal(new[] { "a", "b" }, block[0]);
        Assert.Equal(new[] { "c", "d" }, block[1]);
    }

    [Theory]
    [InlineData("a\tb\r\nc\td")]   // Windows / Excel
    [InlineData("a\tb\rc\td")]     // lone CR
    [InlineData("a\tb\nc\td\n")]   // the trailing newline most copies end with
    public void Every_line_ending_and_a_trailing_newline_give_the_same_block(string text)
    {
        var block = GridPaste.Parse(text);
        Assert.Equal(2, block.Count);
        Assert.Equal(new[] { "c", "d" }, block[1]);
    }

    [Fact]
    public void An_empty_clipboard_is_no_block_at_all()
    {
        Assert.Empty(GridPaste.Parse(null));
        Assert.Empty(GridPaste.Parse(""));
    }

    [Fact]
    public void A_blank_line_inside_the_block_is_kept_as_a_row_of_empties()
    {
        // Only *trailing* blanks are noise; an interior one is a row the user copied and means to clear.
        var block = GridPaste.Parse("a\n\nb");
        Assert.Equal(3, block.Count);
        Assert.Equal(new[] { "" }, block[1]);
    }

    [Fact]
    public void What_copy_produces_is_what_paste_parses()
    {
        var rs = ThreeRows();
        var tsv = GridSelectionOps.Tsv(rs, GridSelectionOps.Rectangle(rs, (rs.Rows[0], 0), (rs.Rows[1], 1)));

        var block = GridPaste.Parse(tsv);
        Assert.Equal(2, block.Count);
        Assert.Equal(new[] { "1", "one" }, block[0]);
        Assert.Equal(new[] { "2", "two" }, block[1]);
    }

    // ---- one value fills the selection --------------------------------------------------------

    [Fact]
    public void A_single_value_fills_every_selected_cell()
    {
        var rs = ThreeRows();
        var selection = Cells(rs, (0, 1), (1, 1), (2, 1));

        var writes = GridPaste.Plan(rs, GridPaste.Parse("x"), (rs.Rows[0], 1), selection);

        Assert.Equal(3, writes.Count);
        Assert.All(writes, w => Assert.Equal("x", w.Text));
        Assert.Equal([(rs.Rows[0], 1), (rs.Rows[1], 1), (rs.Rows[2], 1)],
            writes.Select(w => (w.Row, w.Col)).ToArray());
    }

    [Fact]
    public void A_single_value_fills_a_non_rectangular_selection_too()
    {
        var rs = ThreeRows();
        var writes = GridPaste.Plan(rs, GridPaste.Parse("x"), (rs.Rows[0], 0), Cells(rs, (0, 0), (2, 2)));

        Assert.Equal([(rs.Rows[0], 0), (rs.Rows[2], 2)], writes.Select(w => (w.Row, w.Col)).ToArray());
    }

    [Fact]
    public void A_single_value_with_nothing_selected_lands_on_the_cursor()
    {
        var rs = ThreeRows();
        var writes = GridPaste.Plan(rs, GridPaste.Parse("x"), (rs.Rows[1], 2), Array.Empty<(object?[], int)>());

        Assert.Equal((rs.Rows[1], 2, "x"), writes.Single());
    }

    // ---- a block anchors at the cursor --------------------------------------------------------

    [Fact]
    public void A_block_anchors_at_the_cursor_and_fills_right_and_down()
    {
        var rs = ThreeRows();
        var writes = GridPaste.Plan(rs, GridPaste.Parse("a\tb\nc\td"), (rs.Rows[0], 1), Cells(rs, (0, 1)));

        Assert.Equal(
        [
            (rs.Rows[0], 1, "a"), (rs.Rows[0], 2, "b"),
            (rs.Rows[1], 1, "c"), (rs.Rows[1], 2, "d"),
        ], writes.ToArray());
    }

    [Fact]
    public void A_block_extends_past_a_single_cell_selection_rather_than_being_clipped_to_it()
    {
        var rs = ThreeRows();
        // Excel's rule, and the one this project picked: the selection says *where*, the clipboard says
        // *how much*. Clipping to one cell would silently throw away three of the four values.
        var writes = GridPaste.Plan(rs, GridPaste.Parse("a\tb\nc\td"), (rs.Rows[0], 1), Cells(rs, (0, 1)));

        Assert.Equal(4, writes.Count);
    }

    [Fact]
    public void A_block_never_appends_rows_and_reports_what_it_dropped()
    {
        var rs = ThreeRows();
        var block = GridPaste.Parse("a\nb\nc\nd"); // 4 rows pasted at the last of 3
        var writes = GridPaste.Plan(rs, block, (rs.Rows[2], 0), Cells(rs, (2, 0)));

        Assert.Equal((rs.Rows[2], 0, "a"), writes.Single());
        Assert.Equal(3, GridPaste.Clipped(rs, block, (rs.Rows[2], 0), Cells(rs, (2, 0))));
    }

    [Fact]
    public void A_block_is_clipped_at_the_last_column()
    {
        var rs = ThreeRows();
        var block = GridPaste.Parse("a\tb\tc");
        var writes = GridPaste.Plan(rs, block, (rs.Rows[0], 2), Cells(rs, (0, 2)));

        Assert.Equal((rs.Rows[0], 2, "a"), writes.Single());
        Assert.Equal(2, GridPaste.Clipped(rs, block, (rs.Rows[0], 2), Cells(rs, (0, 2))));
    }

    [Fact]
    public void A_fill_never_counts_as_clipped()
    {
        var rs = ThreeRows();
        var block = GridPaste.Parse("x");
        Assert.Equal(0, GridPaste.Clipped(rs, block, (rs.Rows[0], 0), Cells(rs, (0, 0), (1, 0))));
    }

    [Fact]
    public void A_block_anchored_on_a_row_that_is_gone_writes_nothing()
    {
        var rs = ThreeRows();
        // A discarded pending-new row leaves the cursor pointing at an array that is no longer in Rows.
        var stranded = new object?[] { 9, "gone", 0 };
        Assert.Empty(GridPaste.Plan(rs, GridPaste.Parse("a\tb"), (stranded, 0), Cells(rs, (0, 0))));
    }

    // ---- what the writes then do --------------------------------------------------------------

    [Fact]
    public void Applying_a_plan_marks_exactly_the_pasted_rows_edited()
    {
        var rs = ThreeRows();
        var writes = GridPaste.Plan(rs, GridPaste.Parse("a\tb"), (rs.Rows[1], 1), Cells(rs, (1, 1)));
        foreach (var (row, col, text) in writes) rs.SetCell(row, col, text);

        Assert.True(rs.IsRowEdited(rs.Rows[1]));
        Assert.False(rs.IsRowEdited(rs.Rows[0]));
        Assert.False(rs.IsRowEdited(rs.Rows[2]));
        Assert.Equal("a", rs.Rows[1][1]);
        Assert.Equal("b", rs.Rows[1][2]);
    }

    [Fact]
    public void Pasted_text_goes_in_raw_so_the_save_path_coerces_it_like_a_typed_edit()
    {
        // The paste deliberately does not parse values itself: SetCell stores the text and
        // ResultEditModel.Coerce turns it into the column's type at save time (one value path, not two).
        var rs = ThreeRows();
        var writes = GridPaste.Plan(rs, GridPaste.Parse("42"), (rs.Rows[0], 2), Cells(rs, (0, 2)));
        foreach (var (row, col, text) in writes) rs.SetCell(row, col, text);

        Assert.Equal("42", rs.Rows[0][2]);

        var changes = ResultEditModel.BuildPendingChanges(rs, Target);
        Assert.Contains("42", ResultEditModel.InlineParameters(changes.Single().Command));
    }
}
