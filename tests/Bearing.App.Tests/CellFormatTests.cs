using System;
using System.Globalization;
using Bearing.App.Formatting;
using Xunit;

namespace Bearing.App.Tests;

public class CellFormatTests
{
    [Fact]
    public void DateTime_uses_the_iso_pattern()
        => Assert.Equal("2022-02-15 10:05:03", CellFormat.Display(new DateTime(2022, 2, 15, 10, 5, 3)));

    [Fact]
    public void DateOnly_and_TimeOnly_patterns()
    {
        Assert.Equal("2022-02-15", CellFormat.Display(new DateOnly(2022, 2, 15)));
        Assert.Equal("10:05:03", CellFormat.Display(new TimeOnly(10, 5, 3)));
    }

    [Fact]
    public void Whole_seconds_render_without_a_fractional_part()
    {
        // ".FFFFFF" must drop the separator too, or every timestamp would read "…:03." on the hour.
        Assert.Equal("2022-02-15 10:05:03", CellFormat.Display(new DateTime(2022, 2, 15, 10, 5, 3)));
        Assert.Equal("10:05:00", CellFormat.Display(new TimeOnly(10, 5)));
    }

    [Fact]
    public void Sub_second_precision_is_shown_rather_than_silently_truncated()
    {
        // Postgres stores microseconds; a copied value that isn't the stored one is worse than a long one.
        var dt = new DateTime(2022, 2, 15, 10, 5, 3).AddTicks(1234560); // .123456
        Assert.Equal("2022-02-15 10:05:03.123456", CellFormat.Display(dt));
    }

    [Fact]
    public void DateTimeOffset_keeps_its_offset()
    {
        // A timestamptz used to render through the plain date/time pattern, which dropped the zone — so
        // copying one lost information that couldn't be recovered from the clipboard text.
        var dto = new DateTimeOffset(2022, 2, 15, 10, 5, 3, TimeSpan.FromHours(2));
        Assert.Equal("2022-02-15 10:05:03+02:00", CellFormat.Display(dto));
        Assert.Equal("2022-02-15 08:05:03+00:00", CellFormat.Display(dto.ToUniversalTime()));
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
    public void Round_trips_a_fractional_timestamp_and_an_offset()
    {
        var dt = new DateTime(2022, 2, 15, 10, 5, 3).AddTicks(1234560);
        Assert.True(CellFormat.TryParseDate(CellFormat.Display(dt), typeof(DateTime), out var back));
        Assert.Equal(dt, back);

        var dto = new DateTimeOffset(2022, 2, 15, 10, 5, 3, TimeSpan.FromHours(2));
        Assert.True(CellFormat.TryParseDate(CellFormat.Display(dto), typeof(DateTimeOffset), out var backOffset));
        Assert.Equal(dto, backOffset);              // same instant *and* same offset
        Assert.Equal(dto.Offset, ((DateTimeOffset)backOffset!).Offset);
    }

    [Fact]
    public void Parse_is_unambiguous_whatever_the_machine_culture_reads_first()
    {
        // The whole point of the ISO switch: "2026-04-03" is the 3rd of April everywhere. The old day-first
        // display pattern meant an edited date depended on which way the current culture read 03.04.2026.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US"); // month-first
            Assert.True(CellFormat.TryParseDate("2026-04-03 12:00:00", typeof(DateTime), out var us));
            CultureInfo.CurrentCulture = new CultureInfo("hr-HR"); // day-first
            Assert.True(CellFormat.TryParseDate("2026-04-03 12:00:00", typeof(DateTime), out var hr));
            Assert.Equal(new DateTime(2026, 4, 3, 12, 0, 0), us);
            Assert.Equal(us, hr);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Parse_accepts_shorter_and_T_separated_forms_and_rejects_junk()
    {
        Assert.True(CellFormat.TryParseDate("2023-12-01 00:00:00", typeof(DateTime), out var v));
        Assert.Equal(new DateTime(2023, 12, 1), v);
        Assert.True(CellFormat.TryParseDate("2023-12-01", typeof(DateTime), out var dateOnlyText));
        Assert.Equal(new DateTime(2023, 12, 1), dateOnlyText);
        Assert.True(CellFormat.TryParseDate("2023-12-01T08:30", typeof(DateTime), out var tForm));
        Assert.Equal(new DateTime(2023, 12, 1, 8, 30, 0), tForm);
        Assert.True(CellFormat.TryParseDate("08:30", typeof(TimeOnly), out var t));
        Assert.Equal(new TimeOnly(8, 30), t);
        Assert.False(CellFormat.TryParseDate("not a date", typeof(DateTime), out _));
    }

    [Fact]
    public void Arrays_render_as_brace_lists_not_type_names()
    {
        Assert.Equal("[Trailers, Deleted Scenes]", CellFormat.Display(new[] { "Trailers", "Deleted Scenes" }));
        Assert.Equal("[1, 2, 3]", CellFormat.Display(new[] { 1, 2, 3 }));
        Assert.Equal("[]", CellFormat.Display(Array.Empty<string>()));
        Assert.Equal("[a, (null), b]", CellFormat.Display(new string?[] { "a", null, "b" })); // element nulls
    }

    [Fact]
    public void Multidimensional_arrays_flatten_instead_of_throwing()
    {
        // Postgres int[][] / text[][] come back as a rank-2 Array; the old single-index GetValue threw.
        var grid = new[,] { { 1, 2 }, { 3, 4 } };
        Assert.Equal("[1, 2, 3, 4]", CellFormat.Display(grid)); // row-major flatten, no exception
    }

    [Fact]
    public void Bytea_renders_as_hex_and_truncates_when_long()
    {
        Assert.Equal(@"\xdeadbeef", CellFormat.Display(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
        var long20 = new byte[20];
        var display = CellFormat.Display(long20);
        Assert.StartsWith(@"\x", display);
        Assert.Contains("(20 bytes)", display);
    }
}
