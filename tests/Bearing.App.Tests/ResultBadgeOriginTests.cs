using System;
using System.Collections.Generic;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The PK and FK badges, resolved from the <b>name</b> form of column origin — the only form
/// <c>SqlDataReader</c> can report, and the one the badge detection did not read. It went straight to
/// <c>BaseTableId</c>, which is 0 for every SQL Server column, so <c>ColumnsOf(0)</c> came back empty and
/// no SQL Server result ever showed a badge. The id form is asserted beside each case as the contrast.
/// </summary>
public class ResultBadgeOriginTests
{
    private const long OrdersId = 12;
    private const long CustomersId = 11;

    /// <summary>dbo.Customers(CustomerId pk) &lt;- dbo.Orders(OrderId pk, CustomerId fk, Freight).</summary>
    private static SchemaSnapshot Snapshot() => new(
        "Northwind",
        new[] { "dbo" },
        new[]
        {
            new TableInfo(CustomersId, "dbo", "Customers", RelationKind.Table),
            new TableInfo(OrdersId, "dbo", "Orders", RelationKind.Table),
        },
        new[]
        {
            new ColumnInfo(CustomersId, 1, "CustomerId", "int", true, true),
            new ColumnInfo(OrdersId, 1, "OrderId", "int", true, true),
            new ColumnInfo(OrdersId, 2, "CustomerId", "int", true, false),
            new ColumnInfo(OrdersId, 3, "Freight", "money", false, false),
        },
        new[]
        {
            new ForeignKeyInfo(9101, "FK_Orders_Customers",
                ParentTableId: OrdersId, ParentOrdinals: new[] { 2 },
                ReferencedTableId: CustomersId, ReferencedOrdinals: new[] { 1 }),
        });

    private static ColumnDescriptor Named(string column)
        => new(column, "int", typeof(int),
            BaseSchemaName: "dbo", BaseTableName: "Orders", BaseColumnName: column,
            BaseCatalogName: "Northwind");

    /// <summary>`select * from dbo.Orders` on SQL Server: names only, ids left at 0.</summary>
    private static IReadOnlyList<ColumnDescriptor> ByName()
        => new[] { Named("OrderId"), Named("CustomerId"), Named("Freight") };

    /// <summary>The same result as Postgres describes it: table id + attribute number, no names.</summary>
    private static IReadOnlyList<ColumnDescriptor> ById()
        => new[]
        {
            new ColumnDescriptor("OrderId", "int", typeof(int), OrdersId, 1),
            new ColumnDescriptor("CustomerId", "int", typeof(int), OrdersId, 2),
            new ColumnDescriptor("Freight", "money", typeof(decimal), OrdersId, 3),
        };

    [Fact]
    public void The_pk_badge_is_found_through_a_name_origin()
    {
        Assert.Equal(new[] { 0 }, ResultSetBuilder.DetectPrimaryKeyColumns(Snapshot(), ByName()));
        Assert.Equal(new[] { 0 }, ResultSetBuilder.DetectPrimaryKeyColumns(Snapshot(), ById()));
    }

    [Fact]
    public void The_fk_badge_is_found_through_a_name_origin()
    {
        Assert.Equal(new[] { 1 }, ResultSetBuilder.DetectForeignKeyColumns(Snapshot(), ByName()));
        Assert.Equal(new[] { 1 }, ResultSetBuilder.DetectForeignKeyColumns(Snapshot(), ById()));
    }

    /// <summary>An expression column carries no origin in either form, and must not pick up a badge from
    /// the zeros it leaves behind.</summary>
    [Fact]
    public void An_expression_column_gets_no_badge()
    {
        var cols = new[] { new ColumnDescriptor("n", "int", typeof(int)) };

        Assert.Empty(ResultSetBuilder.DetectPrimaryKeyColumns(Snapshot(), cols));
        Assert.Empty(ResultSetBuilder.DetectForeignKeyColumns(Snapshot(), cols));
    }

    /// <summary>No snapshot means no resolution — the badges are catalog facts, not guesses off the
    /// result.</summary>
    [Fact]
    public void Without_a_snapshot_there_are_no_badges()
    {
        Assert.Empty(ResultSetBuilder.DetectPrimaryKeyColumns(null, ByName()));
        Assert.Empty(ResultSetBuilder.DetectForeignKeyColumns(null, ByName()));
    }
}
