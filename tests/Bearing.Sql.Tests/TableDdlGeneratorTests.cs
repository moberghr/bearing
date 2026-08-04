using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

public class TableDdlGeneratorTests
{
    [Fact]
    public void Renders_columns_primary_key_and_foreign_key()
    {
        var snapshot = TestSchema.Build();
        var orders = snapshot.ResolveTable("public", "orders")!;

        var ddl = TableDdlGenerator.CreateTable(orders, snapshot);

        Assert.Contains("create table \"public\".\"orders\" (", ddl);
        Assert.Contains("\"id\" int4 not null", ddl);
        Assert.Contains("\"total\" numeric", ddl);
        Assert.DoesNotContain("\"total\" numeric not null", ddl);
        Assert.Contains("primary key (\"id\")", ddl);
        Assert.Contains("foreign key (\"user_id\") references \"public\".\"users\" (\"id\")", ddl);
    }
}
