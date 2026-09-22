using System.Text.Json;
using System.Numerics;
using System.Text.Json.Nodes;
using Bearing.Core.Data;
using Bearing.Cli.Tools;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// Values on the way out. A consumer here is a program, so the question every case answers is whether it
/// can be parsed back without knowing anything about the machine that produced it.
/// </summary>
public class ResultJsonTests
{
    private static string Text(object? value) => ResultJson.Value(value)!.GetValue<string>();

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
        var value = new DateTimeOffset(2026, 9, 21, 15, 0, 0, TimeSpan.FromHours(3));

        Assert.Contains("+03:00", Text(value));
    }

    [Fact]
    public void A_date_a_time_and_an_interval_each_render_as_themselves()
    {
        Assert.Equal("2026-09-21", Text(new DateOnly(2026, 9, 21)));
        Assert.StartsWith("15:00:00", Text(new TimeOnly(15, 0)));
        Assert.Equal("1.02:00:00", Text(TimeSpan.FromHours(26)));
    }

    [Fact]
    public void Numbers_stay_numbers_and_null_stays_null()
    {
        // The whole reason this is not the grid's text: a consumer that has to parse 1234 back out of
        // "1,234" is worse off, and a null that became the string "null" collides with a real one.
        Assert.Null(ResultJson.Value(null));
        Assert.Null(ResultJson.Value(DBNull.Value));

        Assert.Equal(JsonValueKind.Number, ResultJson.Value(42)!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, ResultJson.Value(42L)!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, ResultJson.Value(4.25d)!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, ResultJson.Value(12.345m)!.GetValueKind());
        Assert.Equal(JsonValueKind.True, ResultJson.Value(true)!.GetValueKind());
        Assert.Equal(JsonValueKind.String, ResultJson.Value("hello")!.GetValueKind());
    }

    [Fact]
    public void A_decimal_keeps_every_digit_it_had()
    {
        // Not routed through a double on the way out: a numeric column with more precision than a double
        // holds would come back quietly rounded, and nothing downstream could tell.
        Assert.Equal("123456789.123456789", ResultJson.Value(123456789.123456789m)!.ToJsonString());
    }

    [Fact]
    public void A_numeric_too_large_for_decimal_becomes_a_string_rather_than_a_narrowed_number()
    {
        var huge = BigInteger.Pow(10, 40);

        var value = ResultJson.Value(huge)!;

        Assert.Equal(JsonValueKind.String, value.GetValueKind());
        Assert.Equal(huge.ToString(), value.GetValue<string>());
    }

    [Fact]
    public void Bytes_come_back_as_base64_rather_than_an_engine_specific_literal()
    {
        // Postgres spells a bytea \x… and T-SQL spells it 0x…; either would make a consumer branch on which
        // engine answered.
        Assert.Equal("AQID", ResultJson.Value(new byte[] { 1, 2, 3 })!.GetValue<string>());
    }

    [Fact]
    public void A_result_reports_its_columns_its_rows_and_whether_it_was_cut_short()
    {
        var result = new QueryResult(
            Columns: [new ColumnDescriptor("id", "integer", typeof(int)), new ColumnDescriptor("name", "text", typeof(string))],
            Rows: [[1, "one"], [2, null]],
            RowCount: 2,
            Duration: TimeSpan.FromMilliseconds(88),
            Message: null,
            Error: null,
            Truncated: true);

        var json = ResultJson.Of(result);

        Assert.Equal("id", json["columns"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("integer", json["columns"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(2, json["row_count"]!.GetValue<int>());
        Assert.Equal(88, json["duration_ms"]!.GetValue<long>());
        Assert.True(json["truncated"]!.GetValue<bool>());

        var rows = (JsonArray)json["rows"]!;
        Assert.Equal(1, rows[0]![0]!.GetValue<int>());
        Assert.Equal("one", rows[0]![1]!.GetValue<string>());
        // A null cell is a JSON null in its place, so the row still has two entries and column order is
        // still what the columns list says.
        Assert.Equal(2, ((JsonArray)rows[1]!).Count);
        Assert.Null(rows[1]![1]);
    }
}
