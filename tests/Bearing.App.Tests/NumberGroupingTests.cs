using Bearing.App.Formatting;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Digit grouping in the results grid, and — the half that matters more — the guarantee that it stays in
/// the grid. Grouping is a display layer over <see cref="CellFormat.Display"/>; a comma reaching the
/// clipboard, an export or the in-cell editor is the tenfold-write class of bug #26 was, since
/// <see cref="CellFormat.TryParseNumber"/> refuses <c>AllowThousands</c> on purpose.
/// </summary>
public class NumberGroupingTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("42", "42")]
    [InlineData("999", "999")]                    // the last ungrouped integer
    [InlineData("1000", "1,000")]                 // the first grouped one
    [InlineData("110122", "110,122")]
    [InlineData("1234567", "1,234,567")]
    [InlineData("1000000", "1,000,000")]          // a leading group that divides exactly: no leading comma
    [InlineData("100000", "100,000")]
    [InlineData("10000", "10,000")]
    public void Integers_are_grouped_in_threes_from_the_right(string raw, string shown)
        => Assert.Equal(shown, NumberGrouping.Group(raw));

    [Theory]
    [InlineData("-1234567", "-1,234,567")]
    [InlineData("-1000", "-1,000")]
    [InlineData("-999", "-999")]
    [InlineData("+12345", "+12,345")]
    public void A_sign_is_carried_and_not_counted(string raw, string shown)
        => Assert.Equal(shown, NumberGrouping.Group(raw));

    /// <summary>Only the integer part groups. The fraction is digits after the point and never separated —
    /// <c>1,234.567,8</c> would be two conventions in one number.</summary>
    [Theory]
    [InlineData("1234.5", "1,234.5")]
    [InlineData("1234567.891011", "1,234,567.891011")]
    [InlineData("9.5", "9.5")]
    [InlineData("0.123456789", "0.123456789")]
    [InlineData("-45000.5", "-45,000.5")]
    public void Only_the_integer_part_is_grouped(string raw, string shown)
        => Assert.Equal(shown, NumberGrouping.Group(raw));

    /// <summary>What a double renders as at the extremes. The mantissa's integer part is one digit, so
    /// these pass through — asserted so that an exponent is never split by a comma.</summary>
    [Theory]
    [InlineData("1.2345E+21")]
    [InlineData("5E-324")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("")]
    public void Values_with_no_integer_run_to_group_pass_through(string raw)
        => Assert.Equal(raw, NumberGrouping.Group(raw));

    /// <summary>The null token shares the cell path with real values and must survive it untouched.</summary>
    [Fact]
    public void The_null_token_is_left_alone()
        => Assert.Equal(CellFormat.NullToken, NumberGrouping.Group(CellFormat.NullToken));

    /// <summary>A string that merely starts with digits is not a number this may rewrite. Nothing the grid
    /// calls numeric looks like this today; the guard is what keeps that true if something ever does.</summary>
    [Theory]
    [InlineData("1234 days")]
    [InlineData("12345-6789")]
    [InlineData("[1234, 5678]")]
    public void Text_that_only_begins_with_digits_is_refused(string raw)
        => Assert.Equal(raw, NumberGrouping.Group(raw));

    [Fact]
    public void The_setting_is_honoured_both_ways()
    {
        Assert.Equal("1,234,567", NumberGrouping.Apply("1234567", enabled: true));
        Assert.Equal("1234567", NumberGrouping.Apply("1234567", enabled: false));
    }

    // ---- the separation that makes this safe -------------------------------------------------------

    /// <summary>The load-bearing one: grouping must not have reached the shared formatter. Everything below
    /// follows from this, and it is the assertion that fails first if someone "simplifies" the two into one.
    /// </summary>
    [Fact]
    public void CellFormat_renders_numbers_ungrouped()
    {
        Assert.Equal("1234567", CellFormat.Display(1234567));
        Assert.Equal("1234567.89", CellFormat.Display(1234567.89m));
        Assert.Equal("9876543210", CellFormat.Display(9876543210L));
    }

    /// <summary>The cell text every non-visual consumer reads — clipboard, CSV, xlsx, generated DML, the
    /// in-cell editor's seed — is the ungrouped form.</summary>
    [Fact]
    public void The_cell_text_behind_a_copy_is_ungrouped()
    {
        object?[] row = [1234567, 1234.5m];
        Assert.Equal("1234567", GridSelectionOps.CellText(row, 0));
        Assert.Equal("1234.5", GridSelectionOps.CellText(row, 1));
    }

    /// <summary>And a CSV field, explicitly: #26 was exactly this column of the problem.</summary>
    [Fact]
    public void An_exported_field_is_ungrouped()
    {
        Assert.Equal("1234567", TableFormats.Text(1234567));
        Assert.Equal("1234567.89", TableFormats.Text(1234567.89m));
    }

    /// <summary>The round trip a grouped display would break: what the editor is seeded with has to parse
    /// back to the value it came from, and the grouped form deliberately does not parse at all.</summary>
    [Fact]
    public void The_edited_value_round_trips_and_the_grouped_form_is_refused()
    {
        var seed = CellFormat.Display(1234567.89m);
        Assert.True(CellFormat.TryParseNumber(seed, typeof(decimal), out var back));
        Assert.Equal(1234567.89m, back);

        Assert.False(CellFormat.TryParseNumber(NumberGrouping.Group(seed), typeof(decimal), out _));
    }

    /// <summary>The separator does not follow the OS culture, because the decimal point does not either.
    /// Run under a comma-decimal culture, grouping by culture would give <c>1.234.567.89</c>.</summary>
    [Fact]
    public void The_separator_is_invariant_not_the_machines() => CultureScope.In("hr-HR", () =>
        Assert.Equal("1,234,567.89", NumberGrouping.Group(CellFormat.Display(1234567.89m))));

    /// <summary>The quick-stats bar summarises the cells above it, so it has to speak their convention —
    /// and obey the same switch. Both were the machine's culture before.</summary>
    [Fact]
    public void The_stats_bar_agrees_with_the_cells_it_summarises() => CultureScope.In("hr-HR", () =>
    {
        Assert.Equal("1,234.5", NumberGrouping.Apply(CellStats.Format(1234.5), enabled: true));
        Assert.Equal("1,234.5", CellStats.Format(1234.5));
        Assert.Equal("1234.5", NumberGrouping.Apply("1234.5", enabled: false));
    });
}
