using System;
using System.Globalization;
using Squirrel.App.Formatting;
using Xunit;

namespace Squirrel.App.Tests;

public class CellFormatTests
{
    [Fact]
    public void DateTime_uses_day_first_pattern()
        => Assert.Equal("15.02.2022 10:05:03", CellFormat.Display(new DateTime(2022, 2, 15, 10, 5, 3)));

    [Fact]
    public void DateOnly_and_TimeOnly_patterns()
    {
        Assert.Equal("15.02.2022", CellFormat.Display(new DateOnly(2022, 2, 15)));
        Assert.Equal("10:05:03", CellFormat.Display(new TimeOnly(10, 5, 3)));
    }

    [Fact]
    public void Null_shows_a_marker_distinct_from_empty_string()
    {
        Assert.Equal("(null)", CellFormat.Display(null));   // null → marker
        Assert.Equal("", CellFormat.Display(""));           // empty string → blank
        Assert.Equal("42", CellFormat.Display(42));
        Assert.Equal("hello", CellFormat.Display("hello"));
    }

    [Theory]
    [InlineData("(null)", true)]
    [InlineData("(NULL)", true)]
    [InlineData("  (null) ", true)]
    [InlineData("", false)]
    [InlineData("null-ish", false)]
    [InlineData(null, false)]
    public void Null_token_recognition(string? text, bool expected)
        => Assert.Equal(expected, CellFormat.IsNullToken(text));

    [Fact]
    public void Round_trips_through_parse()
    {
        var dt = new DateTime(2022, 2, 15, 10, 5, 3);
        Assert.True(CellFormat.TryParseDate(CellFormat.Display(dt), typeof(DateTime), out var parsed));
        Assert.Equal(dt, parsed);
    }

    [Fact]
    public void Parse_accepts_the_display_pattern_and_rejects_junk()
    {
        Assert.True(CellFormat.TryParseDate("01.12.2023 00:00:00", typeof(DateTime), out var v));
        Assert.Equal(new DateTime(2023, 12, 1), v);
        Assert.False(CellFormat.TryParseDate("not a date", typeof(DateTime), out _));
    }
}
