using System.Globalization;
using System.Numerics;
using Bearing.Cli.Tools;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// Values on the way out. A consumer here is a program, so the question every case answers is whether it
/// can be parsed back without knowing anything about the machine that produced it.
/// <para>
/// Asserted on the CLR value rather than on serialised JSON: the conversion is a pure function of one
/// value, and testing it through a serializer would be testing the serializer.
/// </para>
/// </summary>
public class CellValueTests
{
    private static string Text(object? value) => Assert.IsType<string>(CellValue.For(value));

    /// <summary>
    /// §5.5's distinction, carried on the face of the value. The driver maps <c>timestamptz</c> to
    /// <c>Kind = Utc</c> and <c>timestamp</c> to <c>Unspecified</c>, and those are different facts: one is
    /// an instant, the other a wall time that never had a zone. Appending an offset to the second would
    /// invent information; dropping it from the first would throw the only meaning away.
    /// </summary>
    [Fact]
    public void A_timestamptz_carries_its_zone_and_a_timestamp_carries_none()
    {
        var instant = new DateTime(2026, 9, 21, 15, 0, 0, DateTimeKind.Utc);
        var wallTime = new DateTime(2026, 9, 21, 15, 0, 0, DateTimeKind.Unspecified);

        Assert.EndsWith("Z", Text(instant));

        var zoneless = Text(wallTime);
        Assert.StartsWith("2026-09-21T15:00:00", zoneless);
        Assert.DoesNotContain("Z", zoneless);
        Assert.DoesNotContain("+", zoneless);
    }

    [Fact]
    public void A_timetz_keeps_the_offset_it_was_given()
    {
        Assert.Contains("+03:00", Text(new DateTimeOffset(2026, 9, 21, 15, 0, 0, TimeSpan.FromHours(3))));
    }

    [Fact]
    public void A_date_a_time_and_an_interval_each_render_as_themselves()
    {
        Assert.Equal("2026-09-21", Text(new DateOnly(2026, 9, 21)));
        Assert.StartsWith("15:00:00", Text(new TimeOnly(15, 0)));
        Assert.Equal("1.02:00:00", Text(TimeSpan.FromHours(26)));
    }

    /// <summary>
    /// The whole reason this is not the grid's text: a consumer that has to parse 1234 back out of
    /// "1,234" is worse off, and a null that became the string "null" collides with a real one.
    /// </summary>
    [Fact]
    public void Numbers_stay_numbers_and_null_stays_null()
    {
        Assert.Null(CellValue.For(null));
        Assert.Null(CellValue.For(DBNull.Value));

        Assert.Equal(42, Assert.IsType<int>(CellValue.For(42)));
        Assert.Equal(42L, Assert.IsType<long>(CellValue.For(42L)));
        Assert.Equal(4.25d, Assert.IsType<double>(CellValue.For(4.25d)));
        Assert.Equal(12.345m, Assert.IsType<decimal>(CellValue.For(12.345m)));
        Assert.True(Assert.IsType<bool>(CellValue.For(true)));
        Assert.Equal("hello", Assert.IsType<string>(CellValue.For("hello")));
    }

    [Fact]
    public void A_decimal_keeps_every_digit_it_had()
    {
        // Not routed through a double on the way out: a numeric column with more precision than a double
        // holds would come back quietly rounded, and nothing downstream could tell.
        Assert.Equal(123456789.123456789m, Assert.IsType<decimal>(CellValue.For(123456789.123456789m)));
    }

    [Fact]
    public void A_numeric_too_large_for_decimal_becomes_a_string_rather_than_a_narrowed_number()
    {
        var huge = BigInteger.Pow(10, 40);

        Assert.Equal(huge.ToString(CultureInfo.InvariantCulture), Text(huge));
    }

    [Fact]
    public void Bytes_come_back_as_base64_rather_than_an_engine_specific_literal()
    {
        // Postgres spells a bytea \x… and T-SQL spells it 0x…; either would make a consumer branch on
        // which engine answered.
        Assert.Equal("AQID", Text(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void A_result_reports_its_columns_its_rows_and_whether_it_was_cut_short()
    {
        var result = new QueryResult(
            Columns:
            [
                new ColumnDescriptor("id", "integer", typeof(int)),
                new ColumnDescriptor("name", "text", typeof(string)),
            ],
            Rows: [[1, "one"], [2, null]],
            RowCount: 2,
            Duration: TimeSpan.FromMilliseconds(88),
            Message: null,
            Error: null,
            Truncated: true);

        var set = CellValue.SetOf(result);

        Assert.Equal(["id", "name"], set.Columns.Select(c => c.Name));
        Assert.Equal(["integer", "text"], set.Columns.Select(c => c.Type));
        Assert.Equal(2, set.RowCount);
        Assert.Equal(88, set.DurationMs);
        Assert.True(set.Truncated);
        Assert.Null(set.Message);

        Assert.Equal(1, set.Rows[0][0]);
        Assert.Equal("one", set.Rows[0][1]);
        // A null cell keeps its place, so the row still has two entries and column order is still what the
        // columns list says.
        Assert.Equal(2, set.Rows[1].Count);
        Assert.Null(set.Rows[1][1]);
    }

    [Fact]
    public void A_statement_with_no_grid_keeps_the_only_answer_it_has()
    {
        var result = new QueryResult(
            Columns: [], Rows: [], RowCount: 0, Duration: TimeSpan.Zero,
            Message: "UPDATE 3", Error: null, Truncated: false);

        Assert.Equal("UPDATE 3", CellValue.SetOf(result).Message);
    }
}
