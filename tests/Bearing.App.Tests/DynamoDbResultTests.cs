using System;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// DynamoDB results are never editable in the grid, and the lock chip says why with the engine's own sentence
/// (<see cref="ProviderTraits.GridEditUnavailable"/>) rather than the resolver's "a column is a computed
/// expression" — a false claim for a result that has base columns or none. SC16, <i>Behaviour 10</i>.
/// </summary>
public class DynamoDbResultTests
{
    private const long OrdersId = 1;

    /// <summary>Orders: partition key customerId, sort key orderDate — a shape the resolver would call editable
    /// on any engine whose traits do not say otherwise.</summary>
    private static readonly SchemaSnapshot Orders = new(
        "eu-west-1",
        [""],
        [new TableInfo(OrdersId, "", "Orders", RelationKind.Table)],
        [
            new ColumnInfo(OrdersId, 1, "customerId", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 2, "orderDate", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 3, "status", "S", NotNull: false, IsPrimaryKey: false),
        ],
        [],
        searchPath: [""]);

    private static QueryResult Result(bool withOrigin)
    {
        ColumnDescriptor Column(string name, int ordinal)
            => withOrigin
                ? new ColumnDescriptor(name, "S", typeof(string), OrdersId, ordinal)
                : new ColumnDescriptor(name, "S", typeof(string));

        var columns = new[] { Column("customerId", 1), Column("orderDate", 2), Column("status", 3) };
        var rows = new[] { new object?[] { "c1", "2026-01-01", "open" } };
        return new QueryResult(columns, rows, 1, TimeSpan.Zero, null, null, Truncated: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_result_is_locked_with_the_engines_own_sentence_whatever_the_columns_say(bool withOrigin)
    {
        var sets = ResultSetBuilder.BuildResultSets(
            [Result(withOrigin)], "select * from \"Orders\"", Orders, ProviderTraits.DynamoDb);

        var set = Assert.Single(sets);
        Assert.Null(set.EditTarget);
        Assert.False(set.IsEditable);
        Assert.Equal("Read-only — editing isn't available on Amazon DynamoDB connections yet.", set.LockReason);
        Assert.Equal(ProviderTraits.DynamoDb.GridEditUnavailable, set.LockReason);
    }

    [Fact]
    public void The_resolvers_computed_expression_claim_is_never_made_here()
    {
        var sets = ResultSetBuilder.BuildResultSets(
            [Result(withOrigin: false)], "select * from \"Orders\"", Orders, ProviderTraits.DynamoDb);

        Assert.DoesNotContain("computed expression", sets[0].LockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_lock_applies_with_no_schema_snapshot_too()
    {
        var sets = ResultSetBuilder.BuildResultSets(
            [Result(withOrigin: false)], "select * from \"Orders\"", snapshot: null, ProviderTraits.DynamoDb);

        Assert.Equal(ProviderTraits.DynamoDb.GridEditUnavailable, sets[0].LockReason);
        Assert.Null(sets[0].EditTarget);
    }

    /// <summary>A result with no columns (a statement that returned nothing) has no grid to lock, so no chip.</summary>
    [Fact]
    public void A_result_with_no_columns_carries_no_lock_reason()
    {
        var empty = new QueryResult([], [], 0, TimeSpan.Zero, null, null, Truncated: false);

        var sets = ResultSetBuilder.BuildResultSets([empty], "delete from \"Orders\"", Orders, ProviderTraits.DynamoDb);

        Assert.Null(sets[0].LockReason);
    }

    /// <summary>The control: the same result on an engine that does edit in the grid still resolves a target, so
    /// the lock above is the traits' doing and not a fixture that could never have been editable.</summary>
    [Fact]
    public void The_same_result_on_postgres_is_editable_and_has_no_lock_reason()
    {
        var sets = ResultSetBuilder.BuildResultSets(
            [Result(withOrigin: true)], "select * from orders", Orders, ProviderTraits.Postgres);

        Assert.NotNull(sets[0].EditTarget);
        Assert.True(sets[0].IsEditable);
        Assert.Null(sets[0].LockReason);
    }
}
