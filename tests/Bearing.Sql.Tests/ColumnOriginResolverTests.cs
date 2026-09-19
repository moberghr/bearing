using System.Collections.Generic;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Column origin in its <b>name</b> form — what <c>SqlDataReader</c> reports, and the only form SQL Server
/// can produce. It was accepted by <c>ColumnDescriptor.HasBaseColumn</c> and by
/// <see cref="EditabilityResolver"/>, but <see cref="ForeignKeyResolver"/> and the PK badge read
/// <c>BaseTableId</c> straight off the descriptor — 0 for every SQL Server column — so they asked the
/// snapshot about table 0 and matched ordinal 0. Nothing failed: the grid edited happily while no FK badge,
/// no ↗ affordance and no PK badge ever appeared on a SQL Server result.
/// <para>
/// The Postgres id form is asserted alongside each case, because "it works now" is not a claim one half of
/// a pair can make.
/// </para>
/// </summary>
public class ColumnOriginResolverTests
{
    private static readonly ISchemaSnapshot TSql = TSqlTestSchema.Build();
    private static readonly ISchemaSnapshot Pg = TestSchema.Build();

    /// <summary>
    /// <c>select * from dbo.Orders</c> as SqlClient describes it under <c>CommandBehavior.KeyInfo</c>: names
    /// only, the id fields left at 0, and the catalog named because a three-part name can reach another
    /// database.
    /// </summary>
    private static IReadOnlyList<ColumnDescriptor> OrdersByName() => new[]
    {
        Named("OrderId"),
        Named("CustomerId"),
        Named("Freight"),
    };

    private static ColumnDescriptor Named(
        string column, string table = "Orders", string schema = "dbo", string? catalog = "Northwind")
        => new(column, "int", typeof(int),
            BaseSchemaName: schema, BaseTableName: table, BaseColumnName: column, BaseCatalogName: catalog);

    // ---- The resolver itself ------------------------------------------------------------------

    [Fact]
    public void A_name_origin_resolves_to_its_catalog_table_and_column()
    {
        var origin = ColumnOriginResolver.Resolve(TSql, Named("CustomerId"));

        Assert.NotNull(origin);
        Assert.Equal(TSqlTestSchema.OrdersId, origin!.Table.Id);
        Assert.Equal("Orders", origin.Table.Name);
        Assert.Equal(2, origin.Column.Ordinal);
        Assert.Equal("CustomerId", origin.Column.Name);
    }

    [Fact]
    public void An_id_origin_still_resolves_the_same_way()
    {
        var origin = ColumnOriginResolver.Resolve(
            Pg, new ColumnDescriptor("user_id", "int4", typeof(int), TestSchema.OrdersId, 2));

        Assert.NotNull(origin);
        Assert.Equal(TestSchema.OrdersId, origin!.Table.Id);
        Assert.Equal("user_id", origin.Column.Name);
    }

    /// <summary>SQL Server's default collation is case-insensitive, and the name arrives spelled however
    /// the query spelled it.</summary>
    [Fact]
    public void Names_are_matched_case_insensitively()
    {
        var origin = ColumnOriginResolver.Resolve(TSql, Named("customerid", table: "ORDERS", schema: "DBO"));

        Assert.NotNull(origin);
        Assert.Equal(2, origin!.Column.Ordinal);
    }

    /// <summary>
    /// A name is unique only within a database, so <c>reporting.dbo.Orders</c> on a connection whose
    /// database is <c>Northwind</c> must not resolve to the local table of the same name — the mistake
    /// <see cref="EditabilityResolver"/> refuses for the same reason.
    /// </summary>
    [Fact]
    public void A_name_from_another_database_does_not_resolve()
        => Assert.Null(ColumnOriginResolver.Resolve(TSql, Named("CustomerId", catalog: "reporting")));

    /// <summary>A driver that reports no catalog at all is trusted: it is saying nothing about the
    /// database, not naming a different one.</summary>
    [Fact]
    public void A_missing_catalog_name_is_not_treated_as_a_mismatch()
        => Assert.NotNull(ColumnOriginResolver.Resolve(TSql, Named("CustomerId", catalog: null)));

    [Fact]
    public void An_expression_column_has_no_origin()
        => Assert.Null(ColumnOriginResolver.Resolve(TSql, new ColumnDescriptor("n", "int", typeof(int))));

    [Fact]
    public void A_table_or_column_outside_the_snapshot_does_not_resolve()
    {
        Assert.Null(ColumnOriginResolver.Resolve(TSql, Named("CustomerId", table: "NoSuchTable")));
        Assert.Null(ColumnOriginResolver.Resolve(TSql, Named("NoSuchColumn")));
    }

    // ---- What was actually broken: FK navigation off a name origin ----------------------------

    [Fact]
    public void A_name_origin_fk_column_resolves_to_the_referenced_table()
    {
        var target = ForeignKeyResolver.Resolve(TSql, OrdersByName(), clickedColumn: 1);

        Assert.NotNull(target);
        Assert.Equal("dbo", target!.RefSchema);
        Assert.Equal("Customers", target.RefTable);
        Assert.Equal(new[] { "CustomerId" }, target.RefColumns);
        Assert.Equal(new[] { 1 }, target.SourceColumnIndices);
    }

    [Fact]
    public void Non_fk_and_referenced_side_columns_still_do_not_navigate()
    {
        var cols = OrdersByName();
        Assert.Null(ForeignKeyResolver.Resolve(TSql, cols, 0));   // Orders.OrderId — PK, not a referencing FK
        Assert.Null(ForeignKeyResolver.Resolve(TSql, cols, 2));   // Orders.Freight — a plain column

        // Customers.CustomerId is the referenced side.
        var customers = new[] { Named("CustomerId", table: "Customers") };
        Assert.Null(ForeignKeyResolver.Resolve(TSql, customers, 0));
    }

    /// <summary>
    /// The name-origin table is found, but its FK's <em>other</em> key column has to be located in the
    /// result too — <c>FindResultColumn</c> compared <c>BaseTableId</c>/<c>BaseColumnOrdinal</c>, which are
    /// both 0 here, so it would have matched the first name-origin column of any table.
    /// </summary>
    [Fact]
    public void The_fk_column_is_located_by_its_own_origin_not_by_position()
    {
        // The FK column is last, and a same-named column of another table sits ahead of it.
        var cols = new[]
        {
            Named("CustomerId", table: "Customers"),
            Named("Freight"),
            Named("CustomerId"),
        };

        var target = ForeignKeyResolver.Resolve(TSql, cols, clickedColumn: 2);

        Assert.NotNull(target);
        Assert.Equal(new[] { 2 }, target!.SourceColumnIndices);
    }
}
