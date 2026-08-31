using System;
using System.Collections.Generic;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Initial column sizing for the results grid (#30). The grid used to leave every column on the DataGrid's
/// <c>Auto</c> width, so one long value made its column swallow the viewport and pushed the rest off screen.
/// These cover the arithmetic that replaced it: which side wins, the clamps, and picking the widest sampled
/// value.
/// <para>
/// The arithmetic is only half the answer, and on its own it was a green suite over a visibly clipped column
/// (#73) — it agreed with itself while the text it described did not fit. The other half is
/// <c>Ui.ColumnWidthTests</c>, which asks a realized cell whether the renderer had to trim.
/// </para>
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

    /// <summary>Stands in for the caller's real text measurement. Uniform per character on purpose: what is
    /// under test here is the arithmetic around the measurement, not the measurement.</summary>
    private static double Measure(string text) => text.Length * Char;

    /// <summary>One sampling pass over the single column these fixtures build, at the cell's 60-char
    /// inline threshold.</summary>
    private static ColumnWidths.ColumnSample Sample(IReadOnlyList<object?[]> rows)
        => ColumnWidths.Sample(rows, 0, maxInlineChars: 60);

    private static double Width(string header, IReadOnlyList<object?[]> rows, double cellExtra = 10)
        => ColumnWidths.Initial(
            Measure(header), headerExtra: 17, Measure(ColumnWidths.WidestValue(rows, 0)), cellExtra);

    [Fact]
    public void A_column_is_sized_by_its_widest_loaded_value()
        => Assert.Equal("abcdefghij", ColumnWidths.WidestValue(Rows("ab", "abcdefghij", "abcd"), 0));

    [Fact]
    public void Several_candidates_are_offered_widest_first()
    {
        // The caller measures them all and takes the widest: a character count only picks the widest string
        // on a truly monospace face, and MonoFont is a fallback stack that can land on a proportional one.
        var candidates = Sample(Rows("a", "abcd", "ab", "abcdef", "abc")).Candidates;
        Assert.Equal(["abcdef", "abcd", "abc", "ab"], candidates);
    }

    [Fact]
    public void A_narrower_but_longer_string_does_not_hide_a_wide_one()
    {
        // The proportional-fallback case: 'W' draws far wider than 'i', so a column full of longer i-strings
        // must not push the genuinely wide value out of the shortlist — however many of them there are.
        var candidates = Sample(Rows(
            "WWWWWWWWWW", "iiiiiiiiiii", "iiiiiiiiiii", "iiiiiiiiiii", "iiiiiiiiiii", "iiiiiiiiiii")).Candidates;
        Assert.Contains("WWWWWWWWWW", candidates);
    }

    [Fact]
    public void There_is_always_something_to_measure()
        // The caller measures unconditionally, so an empty result cannot yield an empty list.
        => Assert.Equal([""], Sample(new List<object?[]>()).Candidates);

    [Fact]
    public void The_inspect_affordance_is_reported_from_the_same_pass()
    {
        // Both answers come off the same strings; scanning twice meant formatting every sampled cell twice
        // for every column, at grid-build time.
        Assert.False(Sample(Rows("short", "also short")).AnyInspectable);
        Assert.True(Sample(Rows("short", new string('x', 80))).AnyInspectable);
        Assert.True(Sample(Rows("first line\nand more")).AnyInspectable);
    }

    [Fact]
    public void A_multiline_value_is_only_as_wide_as_its_first_line()
        // The cell renders one trimmed line, so the rest of the document must not widen the column.
        => Assert.Equal("abc", ColumnWidths.WidestValue(Rows("abc\nabcdefghijklmnop"), 0));

    [Fact]
    public void Null_is_measured_as_the_marker_the_cell_actually_shows()
        => Assert.Equal("(null)", ColumnWidths.WidestValue(Rows(null, null), 0));

    [Fact]
    public void Only_the_sampled_rows_count()
        => Assert.Equal("ab", ColumnWidths.WidestValue(Rows("ab", "abcdefghij"), 0, sample: 1));

    [Fact]
    public void A_short_row_is_treated_as_null_not_an_error()
    {
        // A pending-new row is created at the result's width, but a projected row can still be short.
        var rows = new List<object?[]> { new object?[] { "ab" }, Array.Empty<object?>() };
        Assert.Equal("(null)", ColumnWidths.WidestValue(rows, 0));
    }

    [Fact]
    public void An_enormous_value_is_not_measured_in_full()
    {
        // Past the scan cap the answer cannot change — Max is reached long before — so the caller is handed
        // a bounded string to measure rather than a 5 MB document.
        var widest = ColumnWidths.WidestValue(Rows(new string('x', 100_000)), 0);
        Assert.Equal(200, widest.Length);
    }

    [Fact]
    public void A_wide_value_is_capped_so_its_neighbours_stay_on_screen()
        => Assert.Equal(ColumnWidths.Max, Width("note", Rows(new string('x', 500))));

    [Fact]
    public void A_narrow_column_still_opens_wide_enough_to_grab()
        => Assert.Equal(ColumnWidths.Min, Width("id", Rows(1, 2, 3)));

    [Fact]
    public void A_long_header_widens_a_column_of_short_values()
    {
        // 'release_year' over two-digit values: the header, not the data, sets the width.
        Assert.Equal(12 * Char + 17, Width("release_year", Rows(11, 22)));
    }

    [Fact]
    public void Values_widen_a_column_past_a_short_header()
    {
        // 'title' over film titles: the data wins, and lands well short of the cap.
        var width = Width("title", Rows(new string('t', 24)));
        Assert.Equal(24 * Char + 10, width);
        Assert.True(width < ColumnWidths.Max);
    }

    [Fact]
    public void A_reserved_glyph_widens_the_column_rather_than_eating_the_value()
    {
        var rows = Rows(new string('v', 12));
        var plain = Width("note", rows);
        var withGlyph = Width("note", rows, cellExtra: 50);
        Assert.Equal(40, withGlyph - plain);
    }

    [Fact]
    public void An_empty_result_falls_back_to_the_header()
    {
        Assert.Equal("", ColumnWidths.WidestValue(new List<object?[]>(), 0));
        Assert.Equal(ColumnWidths.Min, Width("abc", new List<object?[]>()));
    }
}
