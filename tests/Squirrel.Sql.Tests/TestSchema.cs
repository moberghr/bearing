using Squirrel.Core.Schema;

namespace Squirrel.Sql.Tests;

/// <summary>A tiny hand-built schema for completion tests: users(1) &lt;- orders(2).</summary>
internal static class TestSchema
{
    public const uint UsersOid = 1;
    public const uint OrdersOid = 2;

    public static SchemaSnapshot Build()
    {
        var tables = new[]
        {
            new PgTable(UsersOid, "public", "users", PgRelKind.Table),
            new PgTable(OrdersOid, "public", "orders", PgRelKind.Table),
        };

        var columns = new[]
        {
            new PgColumn(UsersOid, 1, "id", "int4", true, true),
            new PgColumn(UsersOid, 2, "name", "text", false, false),
            new PgColumn(UsersOid, 3, "email", "text", false, false),
            new PgColumn(OrdersOid, 1, "id", "int4", true, true),
            new PgColumn(OrdersOid, 2, "user_id", "int4", true, false),
            new PgColumn(OrdersOid, 3, "total", "numeric", false, false),
        };

        var fks = new[]
        {
            new PgForeignKey(9001, "orders_user_id_fkey",
                ParentOid: OrdersOid, ParentAttNums: new short[] { 2 },
                ReferencedOid: UsersOid, ReferencedAttNums: new short[] { 1 }),
        };

        return new SchemaSnapshot("testdb", new[] { "public" }, tables, columns, fks);
    }
}
