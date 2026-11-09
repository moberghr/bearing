using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The schema tree's "Select top 100 rows" statement, per engine: where the limit goes is the dialect's call —
/// a trailing <c>limit</c> on Postgres and MySQL, <c>top (n)</c> after <c>select</c> on SQL Server, which has no
/// <c>limit</c> at all.
/// </summary>
public class SelectTopRowsTests
{
    [Fact]
    public void Postgres_appends_limit()
        => Assert.Equal("select * from \"shop\".\"payment\" limit 100",
            ((ISqlDialect)PostgresDialect.Instance).SelectTopRows("\"shop\".\"payment\"", 100));

    [Fact]
    public void Sql_server_uses_top()
        => Assert.Equal("select top (100) * from [dbo].[Orders]",
            ((ISqlDialect)SqlServerDialect.Instance).SelectTopRows("[dbo].[Orders]", 100));

    [Fact]
    public void MySql_appends_limit()
        => Assert.Equal("select * from `shop`.`payment` limit 100",
            ((ISqlDialect)MySqlDialect.Instance).SelectTopRows("`shop`.`payment`", 100));
}
