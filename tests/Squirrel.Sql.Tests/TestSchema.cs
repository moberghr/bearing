using Squirrel.Core.Schema;

namespace Squirrel.Sql.Tests;

/// <summary>A tiny hand-built schema for completion tests: users(1) &lt;- orders(2).</summary>
internal static class TestSchema
{
    public const long UsersId = 1;
    public const long OrdersId = 2;

    public static SchemaSnapshot Build()
    {
        var tables = new[]
        {
            new TableInfo(UsersId, "public", "users", RelationKind.Table),
            new TableInfo(OrdersId, "public", "orders", RelationKind.Table),
        };

        var columns = new[]
        {
            new ColumnInfo(UsersId, 1, "id", "int4", true, true),
            new ColumnInfo(UsersId, 2, "name", "text", false, false),
            new ColumnInfo(UsersId, 3, "email", "text", false, false),
            new ColumnInfo(OrdersId, 1, "id", "int4", true, true),
            new ColumnInfo(OrdersId, 2, "user_id", "int4", true, false),
            new ColumnInfo(OrdersId, 3, "total", "numeric", false, false),
        };

        var fks = new[]
        {
            new ForeignKeyInfo(9001, "orders_user_id_fkey",
                ParentTableId: OrdersId, ParentOrdinals: new[] { 2 },
                ReferencedTableId: UsersId, ReferencedOrdinals: new[] { 1 }),
        };

        return new SchemaSnapshot("testdb", new[] { "public" }, tables, columns, fks);
    }
}
