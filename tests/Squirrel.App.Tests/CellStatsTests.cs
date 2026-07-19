using Squirrel.App.Results;
using Xunit;

namespace Squirrel.App.Tests;

public class CellStatsTests
{
    [Theory]
    [InlineData(typeof(int), true)]
    [InlineData(typeof(long), true)]
    [InlineData(typeof(decimal), true)]
    [InlineData(typeof(double), true)]
    [InlineData(typeof(short), true)]
    [InlineData(typeof(int?), true)]     // Nullable<int> unwraps
    [InlineData(typeof(string), false)]
    [InlineData(typeof(bool), false)]
    [InlineData(typeof(System.DateTime), false)]
    public void IsNumeric_covers_numeric_clr_types(System.Type type, bool expected)
        => Assert.Equal(expected, CellStats.IsNumeric(type));

    [Fact]
    public void Measure_column_excludes_keys_and_non_numeric()
    {
        Assert.True(CellStats.IsMeasureColumn(typeof(decimal), isPrimaryKey: false, isForeignKey: false));
        Assert.False(CellStats.IsMeasureColumn(typeof(int), isPrimaryKey: true, isForeignKey: false));  // PK
        Assert.False(CellStats.IsMeasureColumn(typeof(int), isPrimaryKey: false, isForeignKey: true));  // FK
        Assert.False(CellStats.IsMeasureColumn(typeof(string), isPrimaryKey: false, isForeignKey: false)); // text
    }

    [Theory]
    [InlineData(42, true, 42)]
    [InlineData(3.5, true, 3.5)]
    [InlineData("1234.5", true, 1234.5)]
    [InlineData("not a number", false, 0)]
    [InlineData(null, false, 0)]
    public void TryParseNumber_handles_boxed_and_text(object? value, bool ok, double expected)
    {
        Assert.Equal(ok, CellStats.TryParseNumber(value, out var result));
        if (ok) Assert.Equal(expected, result);
    }

    [Fact]
    public void TryParseNumber_parses_decimal_box()
        => Assert.True(CellStats.TryParseNumber(9.99m, out _));

    [Fact]
    public void Aggregate_computes_count_sum_avg_min_max_and_skips_non_numeric()
    {
        var stats = CellStats.Aggregate(new object?[] { 10, "20", 30.0m, null, "x" });
        Assert.NotNull(stats);
        Assert.Equal(3, stats!.Value.Count);   // 10, 20, 30 (null & "x" skipped)
        Assert.Equal(60, stats.Value.Sum);
        Assert.Equal(20, stats.Value.Avg);
        Assert.Equal(10, stats.Value.Min);
        Assert.Equal(30, stats.Value.Max);
    }

    [Fact]
    public void Aggregate_returns_null_when_nothing_parses()
        => Assert.Null(CellStats.Aggregate(new object?[] { null, "x", "y" }));
}
