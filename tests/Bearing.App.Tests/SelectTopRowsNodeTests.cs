using System;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data.MySql;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Demo;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// "Select top 100 rows" on a table or view row: the statement is written in the row's own connection's dialect,
/// with every name quoted so a mixed-case or keyword name still means its table, and only relation rows offer it.
/// </summary>
public class SelectTopRowsNodeTests
{
    private static ConnectionInfo Conn(string providerId)
        => new() { Id = Guid.NewGuid(), Name = "srv", ProviderId = providerId, Database = "demo", User = "u" };

    // The browser is only consulted when the row is expanded; building the statement never touches it.
    private static RelationNodeViewModel Node(string providerId, TableInfo table)
        => new(Conn(providerId), "demo", table, DemoCatalog.Snapshot(), browser: null!, defaultSchema: "shop");

    [Fact]
    public void Postgres_rows_select_with_limit()
        => Assert.Equal("select * from \"shop\".\"payment\" limit 100",
            Node(PostgresProvider.ProviderId, new TableInfo(1, "shop", "payment", RelationKind.Table)).SelectTopRowsSql);

    [Fact]
    public void Sql_server_rows_select_with_top()
        => Assert.Equal("select top (100) * from [dbo].[Order Lines]",
            Node(SqlServerProvider.ProviderId, new TableInfo(1, "dbo", "Order Lines", RelationKind.Table)).SelectTopRowsSql);

    [Fact]
    public void MySql_rows_select_with_limit()
        => Assert.Equal("select * from `shop`.`order` limit 100",
            Node(MySqlProvider.ProviderId, new TableInfo(1, "shop", "order", RelationKind.Table)).SelectTopRowsSql);

    [Fact]
    public void A_quote_in_a_name_is_escaped_not_closed()
        => Assert.Equal("select * from \"shop\".\"odd\"\"name\" limit 100",
            Node(PostgresProvider.ProviderId, new TableInfo(1, "shop", "odd\"name", RelationKind.Table)).SelectTopRowsSql);

    [Fact]
    public void Views_offer_it_too()
        => Assert.True(Node(PostgresProvider.ProviderId, new TableInfo(1, "shop", "v_sales", RelationKind.View)).CanSelectRows);

    [Fact]
    public void Rows_that_are_not_relations_do_not_offer_it()
    {
        var sequence = new SequenceNodeViewModel(
            new SequenceInfo(1, "shop", "payment_id_seq", "bigint", 1, 1, 1, long.MaxValue, false, null), "shop");
        Assert.False(sequence.CanSelectRows);
    }
}
