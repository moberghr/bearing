using Bearing.App.Results;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Where a zone goes across a one-replace rewrite (<see cref="InlineZoneRemap"/>): by statement when the
/// rewrite keeps the statements, by the text around it otherwise, and nowhere when its text is gone.
/// </summary>
public class InlineZoneRemapTests
{
    private const string Two = "select 1;\nselect 2;\n";

    private static int?[] Map(string before, string after, params int[] offsets)
        => [.. InlineZoneRemap.Map(PostgresDialect.Instance, before, after, offsets)];

    [Fact]
    public void A_reformat_keeps_each_zone_on_its_statement()
    {
        const string after = "SELECT\n  1;\n\nSELECT\n  2;\n";
        Assert.Equal([after.IndexOf("1;") + 2, after.IndexOf("2;") + 2], Map(Two, after, 9, 19));
    }

    [Fact]
    public void Commenting_out_a_statement_moves_the_zones_with_the_text_around_them()
    {
        Assert.Equal([12, 22], Map(Two, "-- select 1;\nselect 2;\n", 9, 19));
    }

    [Fact]
    public void A_zone_whose_text_is_gone_maps_to_nothing()
    {
        Assert.Equal([null, 9], Map(Two, "select 2;\n", 9, 19));
    }

    [Fact]
    public void A_zone_off_any_statement_end_is_not_guessed_at()
    {
        // Same statement count, but 4 is no statement's end: the prefix/suffix rule decides, and 4 lies in
        // the span that changed.
        Assert.Equal([null], Map(Two, "SELECT 1;\nselect 2;\n", 4));
    }
}
