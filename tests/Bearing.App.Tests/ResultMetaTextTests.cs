using System;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The captions that label a result set. A result reaches the grid in one of three shapes — an error, a
/// statement that returned no columns, or rows — and each gets different text; the branch used to live inside
/// <c>ResultView</c> where it couldn't be asserted.
/// </summary>
public class ResultMetaTextTests
{
    private static ResultSetViewModel Rows(long count, int ms) => new(
        new QueryResult(
            [new ColumnDescriptor("id", "int4", typeof(int))],
            new[] { new object?[] { 1 } }, count, TimeSpan.FromMilliseconds(ms), null, null, false),
        "select id from t", pageable: false);

    private static ResultSetViewModel Failed(string message) => new(
        new QueryResult([], Array.Empty<object?[]>(), 0, TimeSpan.Zero, null,
            new QueryError(message, "42601", null), false),
        "oops", pageable: false);

    private static ResultSetViewModel Statement(string? message) => new(
        new QueryResult([], Array.Empty<object?[]>(), 0, TimeSpan.Zero, message, null, false),
        "update t set x = 1", pageable: false);

    [Fact]
    public void A_row_bearing_result_reports_rows_and_duration()
        => Assert.Equal("Result · 10 rows · 88 ms", ResultMetaText.Meta("Result", Rows(10, 88)));

    [Fact]
    public void One_row_is_singular()
        => Assert.Equal("Result · 1 row · 5 ms", ResultMetaText.Meta("Result", Rows(1, 5)));

    [Fact]
    public void A_null_label_falls_back_to_Result()
        => Assert.Equal("Result · 2 rows · 0 ms", ResultMetaText.Meta(null, Rows(2, 0)));

    [Fact]
    public void The_label_carries_the_set_number_in_stacked_view()
        => Assert.Equal("Result 3 · 2 rows · 1 ms", ResultMetaText.Meta("Result 3", Rows(2, 1)));

    [Fact]
    public void A_failed_result_reports_the_error_instead_of_a_count()
        => Assert.Equal("Result · error: syntax error", ResultMetaText.Meta("Result", Failed("syntax error")));

    [Fact]
    public void A_column_less_statement_reports_its_message()
        => Assert.Equal("Result · UPDATE 3", ResultMetaText.Meta("Result", Statement("UPDATE 3")));

    [Fact]
    public void A_column_less_statement_with_no_message_still_says_something()
        => Assert.Equal("Result · Statement executed.", ResultMetaText.Meta("Result", Statement(null)));

    [Fact]
    public void Tab_headers_are_one_based_and_show_the_row_count()
        => Assert.Equal("Result 1 (10)", ResultMetaText.TabHeader(0, Rows(10, 1)));

    [Fact]
    public void A_failed_tab_header_says_error()
        => Assert.Equal("Result 2 · error", ResultMetaText.TabHeader(1, Failed("boom")));

    [Fact]
    public void The_inspector_title_names_the_table_the_key_and_the_column()
    {
        var columns = new[]
        {
            new ColumnDescriptor("film_id", "int4", typeof(int)),
            new ColumnDescriptor("description", "text", typeof(string)),
        };
        var result = new QueryResult(columns, new[] { new object?[] { 42, "long text" } }, 1, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select * from film", pageable: false)
        {
            PrimaryKeyColumns = [0],
            EditTarget = new EditTarget("public", "film",
                [new EditableColumn(0, "film_id", IsPrimaryKey: true), new EditableColumn(1, "description", IsPrimaryKey: false)]),
        };

        Assert.Equal("film[42].description", ResultMetaText.InspectorTitle(rs, 1, rs.Rows[0]));
    }

    [Fact]
    public void An_unattributable_row_with_no_key_still_gets_a_title()
    {
        var columns = new[] { new ColumnDescriptor("payload", "jsonb", typeof(string)) };
        var result = new QueryResult(columns, new[] { new object?[] { "{}" } }, 1, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select '{}'::jsonb as payload", pageable: false);

        // No edit target and no primary key — an expression result, which is exactly when the inspector
        // is most useful, so it must not throw or render blank.
        Assert.Equal("row[?].payload", ResultMetaText.InspectorTitle(rs, 0, rs.Rows[0]));
    }
}
