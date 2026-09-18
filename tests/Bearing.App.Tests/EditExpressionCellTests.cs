using System;
using System.Linq;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// When a typed cell value becomes SQL rather than a value (#149), and — more to the point — when it does
/// not. The decision is two conditions wide (a column the driver does not map to <c>string</c>, and a value
/// <c>Coerce</c> already failed on), and each of them is what stops this changing the meaning of an edit
/// that works today.
/// </summary>
public class EditExpressionCellTests
{
    private static readonly EditTarget Target = new("public", "t",
    [
        new EditableColumn(0, "id", IsPrimaryKey: true),
        new EditableColumn(1, "created_at", IsPrimaryKey: false),
        new EditableColumn(2, "note", IsPrimaryKey: false),
    ]);

    /// <summary>(id int, created_at timestamptz, note text) with one row, originals captured.</summary>
    private static ResultSetViewModel OneRow()
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), 1, 1),
            new ColumnDescriptor("created_at", "timestamptz", typeof(DateTime), 1, 2),
            new ColumnDescriptor("note", "text", typeof(string), 1, 3),
        };
        var row = new object?[] { 1, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), "hello" };
        var result = new QueryResult(columns, new[] { row }, 1, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select * from t", pageable: false) { EditTarget = Target };
        rs.CaptureOriginals();
        return rs;
    }

    [Fact]
    public void Now_in_a_timestamp_cell_is_saved_as_sql_not_as_a_string()
    {
        var rs = OneRow();
        rs.SetCell(rs.Rows[0], 1, "now()");

        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));
        Assert.Contains("\"created_at\" = now()", change.Command.Sql);
        // The value is gone from the parameter list entirely — only the key remains.
        Assert.Equal(new object?[] { 1 }, change.Command.Parameters.Select(p => p.Value));
    }

    /// <summary>The whole reason the rule can be type-directed: on a text column the same six characters are
    /// a legitimate value, and <c>Coerce</c> accepts them, so nothing reinterprets them.</summary>
    [Fact]
    public void Now_in_a_text_cell_stays_the_literal_string()
    {
        var rs = OneRow();
        rs.SetCell(rs.Rows[0], 2, "now()");

        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));
        Assert.Contains("\"note\" = @p0", change.Command.Sql);
        Assert.Equal("now()", change.Command.Parameters[0].Value);
        Assert.Null(ResultEditModel.ExpressionFor("now()", typeof(string)));
    }

    /// <summary>A value that parses as the column's type is that value. Only a value that cannot be written
    /// at all — the set that was already drawn amber — is free to mean something else.</summary>
    [Fact]
    public void A_value_that_parses_is_never_read_as_an_expression()
    {
        Assert.Null(ResultEditModel.ExpressionFor("2026-01-02 03:04:05", typeof(DateTime)));
        Assert.Null(ResultEditModel.ExpressionFor("(null)", typeof(DateTime)));
        Assert.Null(ResultEditModel.ExpressionFor("", typeof(DateTime)));
    }

    /// <summary>An unrecognised value keeps the amber mark it had: the cell says the save will be refused,
    /// which is the signal #149 must not take away by half-recognising things.</summary>
    [Fact]
    public void An_unknown_function_is_still_refused_and_still_marked()
    {
        Assert.Null(ResultEditModel.ExpressionFor("makedate(2026)", typeof(DateTime)));
        Assert.True(ResultEditModel.WillReachServerAsText("makedate(2026)", typeof(DateTime)));

        // …and a recognised one is not amber, because it is not going to be refused.
        Assert.False(ResultEditModel.WillReachServerAsText("now()", typeof(DateTime)));
    }

    [Fact]
    public void An_expression_in_a_new_row_is_inlined_in_the_insert()
    {
        var rs = OneRow();
        var row = rs.AddRow();
        rs.SetCell(row, 1, "now()");
        rs.SetCell(row, 2, "fresh");

        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));
        Assert.Equal(ResultEditModel.ChangeKind.Insert, change.Kind);
        Assert.Contains("values (now(), @p0)", change.Command.Sql);
    }

    /// <summary>What the grid shows after the save is the server's answer, not the text that was typed —
    /// an expression's value exists nowhere else.</summary>
    [Fact]
    public void A_saved_expression_is_replaced_by_the_value_the_server_returned()
    {
        var rs = OneRow();
        var row = rs.Rows[0];
        rs.SetCell(row, 1, "now()");
        var changes = ResultEditModel.BuildPendingChanges(rs, Target);

        var evaluated = new DateTime(2026, 9, 17, 10, 30, 0, DateTimeKind.Utc);
        var returned = new QueryResult(
            new[]
            {
                new ColumnDescriptor("id", "int4", typeof(int)),
                new ColumnDescriptor("created_at", "timestamptz", typeof(DateTime)),
                new ColumnDescriptor("note", "text", typeof(string)),
            },
            new[] { new object?[] { 1, evaluated, "hello" } }, 1, TimeSpan.Zero, null, null, false);

        ResultEditModel.ApplySavedChanges(rs, Target, changes, new[] { returned });

        Assert.Equal(evaluated, rs.Rows[0][1]);
        Assert.False(rs.HasPendingChanges);
    }

    /// <summary>The read-back matches on the <b>base</b> column name, so an aliased result survives it. The
    /// version that built a fresh row from displayed names wrote nulls over columns it could not match.</summary>
    [Fact]
    public void An_aliased_column_keeps_its_value_through_the_read_back()
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), 1, 1),
            new ColumnDescriptor("n", "text", typeof(string), 1, 3),   // select note as n
        };
        var target = new EditTarget("public", "t",
        [
            new EditableColumn(0, "id", IsPrimaryKey: true),
            new EditableColumn(1, "note", IsPrimaryKey: false),
        ]);
        var result = new QueryResult(columns, new[] { new object?[] { 1, "hello" } }, 1, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select id, note as n from t", pageable: false) { EditTarget = target };
        rs.CaptureOriginals();
        rs.SetCell(rs.Rows[0], 1, "changed");
        var changes = ResultEditModel.BuildPendingChanges(rs, target);

        var returned = new QueryResult(
            new[] { new ColumnDescriptor("id", "int4", typeof(int)), new ColumnDescriptor("note", "text", typeof(string)) },
            new[] { new object?[] { 1, "changed" } }, 1, TimeSpan.Zero, null, null, false);
        ResultEditModel.ApplySavedChanges(rs, target, changes, new[] { returned });

        Assert.Equal("changed", rs.Rows[0][1]);
    }
}
