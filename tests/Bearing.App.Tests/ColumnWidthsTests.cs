using System;
using System.Collections.Generic;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Initial column sizing for the results grid (#30). The grid used to leave every column on the DataGrid's
/// <c>Auto</c> width, so one long value made its column swallow the viewport and pushed the rest off screen.
/// These cover the arithmetic that replaced it — the part that can be tested without a grid (§4.3).
/// </summary>
public class ColumnWidthsTests
{
    private const double Char = 7;   // a plausible monospace advance at the grid's font size

    private static List<object?[]> Rows(params object?[] values)
    {
        var rows = new List<object?[]>();
        foreach (var v in values) rows.Add(new[] { v });
        return rows;
    }

    private static double Width(int headerChars, IReadOnlyList<object?[]> rows, double cellExtra = 10)
        => ColumnWidths.Initial(headerChars, headerExtra: 17, ColumnWidths.ValueChars(rows, 0), cellExtra, Char);

    [Fact]
    public void A_column_is_sized_by_its_widest_loaded_value()
    {
        var chars = ColumnWidths.ValueChars(Rows("ab", "abcdefghij", "abcd"), 0);
        Assert.Equal(10, chars);
    }

    [Fact]
    public void A_multiline_value_is_only_as_wide_as_its_first_line()
    {
        // The cell renders one trimmed line, so the rest of the document must not widen the column.
        Assert.Equal(3, ColumnWidths.ValueChars(Rows("abc\nabcdefghijklmnop"), 0));
    }

    [Fact]
    public void Null_is_measured_as_the_marker_the_cell_actually_shows()
        => Assert.Equal("(null)".Length, ColumnWidths.ValueChars(Rows(null, null), 0));

    [Fact]
    public void Only_the_sampled_rows_count()
    {
        var rows = Rows("ab", "abcdefghij");
        Assert.Equal(2, ColumnWidths.ValueChars(rows, 0, sample: 1));
    }

    [Fact]
    public void A_short_row_is_treated_as_null_not_an_error()
    {
        // A pending-new row is created at the result's width, but a projected row can still be short.
        var rows = new List<object?[]> { new object?[] { "ab" }, Array.Empty<object?>() };
        Assert.Equal("(null)".Length, ColumnWidths.ValueChars(rows, 0));
    }

    [Fact]
    public void A_wide_value_is_capped_so_its_neighbours_stay_on_screen()
    {
        var wide = new string('x', 500);
        Assert.Equal(ColumnWidths.Max, Width(headerChars: 4, Rows(wide)));
    }

    [Fact]
    public void A_narrow_column_still_opens_wide_enough_to_grab()
    {
        Assert.Equal(ColumnWidths.Min, Width(headerChars: 2, Rows(1, 2, 3)));
    }

    [Fact]
    public void A_long_header_widens_a_column_of_short_values()
    {
        // 'release_year' over two-digit values: the header, not the data, sets the width.
        var width = Width(headerChars: 12, Rows(11, 22));
        Assert.Equal(12 * Char + 17, width);
    }

    [Fact]
    public void Values_widen_a_column_past_a_short_header()
    {
        // 'title' over film titles: the data wins, and lands well short of the cap.
        var width = Width(headerChars: 5, Rows(new string('t', 24)));
        Assert.Equal(24 * Char + 10, width);
        Assert.True(width < ColumnWidths.Max);
    }

    [Fact]
    public void A_reserved_glyph_widens_the_column_rather_than_eating_the_value()
    {
        var rows = Rows(new string('v', 12));
        var plain = Width(headerChars: 4, rows);
        var withGlyph = Width(headerChars: 4, rows, cellExtra: 50);
        Assert.Equal(40, withGlyph - plain);
    }

    [Fact]
    public void An_empty_result_falls_back_to_the_header()
    {
        Assert.Equal(0, ColumnWidths.ValueChars(new List<object?[]>(), 0));
        Assert.Equal(ColumnWidths.Min, Width(headerChars: 3, new List<object?[]>()));
    }
}
