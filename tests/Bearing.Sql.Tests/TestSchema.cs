using Bearing.Core.Schema;

namespace Bearing.Sql.Tests;

/// <summary>
/// A tiny hand-built schema for completion tests: users(1) &lt;- orders(2), plus the two names that
/// break bare insertion — <c>__MigrationHistory</c>(3) (Postgres folds it to lower case) and
/// <c>order</c>(4) (a reserved keyword) — and <c>audit.events</c>(5), a relation outside search_path.
/// </summary>
internal static class TestSchema
{
    public const long UsersId = 1;
    public const long OrdersId = 2;
    public const long MigrationHistoryId = 3;
    public const long OrderId = 4;
    public const long EventsId = 5;

    public static SchemaSnapshot Build()
    {
        var tables = new[]
        {
            new TableInfo(UsersId, "public", "users", RelationKind.Table),
            new TableInfo(OrdersId, "public", "orders", RelationKind.Table),
            new TableInfo(MigrationHistoryId, "public", "__MigrationHistory", RelationKind.Table),
            new TableInfo(OrderId, "public", "order", RelationKind.Table),
            new TableInfo(EventsId, "audit", "events", RelationKind.Table),
        };

        var columns = new[]
        {
            new ColumnInfo(UsersId, 1, "id", "int4", true, true),
            new ColumnInfo(UsersId, 2, "name", "text", false, false),
            new ColumnInfo(UsersId, 3, "email", "text", false, false),
            new ColumnInfo(OrdersId, 1, "id", "int4", true, true),
            new ColumnInfo(OrdersId, 2, "user_id", "int4", true, false),
            new ColumnInfo(OrdersId, 3, "total", "numeric", false, false),
            new ColumnInfo(MigrationHistoryId, 1, "MigrationId", "text", true, true),
            new ColumnInfo(MigrationHistoryId, 2, "ProductVersion", "text", false, false),
            new ColumnInfo(MigrationHistoryId, 3, "user_id", "int4", false, false),
            new ColumnInfo(OrderId, 1, "id", "int4", true, true),
            new ColumnInfo(OrderId, 2, "total", "numeric", false, false),
            new ColumnInfo(EventsId, 1, "id", "int8", true, true),
            new ColumnInfo(EventsId, 2, "payload", "jsonb", false, false),
        };

        var fks = new[]
        {
            new ForeignKeyInfo(9001, "orders_user_id_fkey",
                ParentTableId: OrdersId, ParentOrdinals: new[] { 2 },
                ReferencedTableId: UsersId, ReferencedOrdinals: new[] { 1 }),
            // A mixed-case relation on the referencing side: both the table name and the referenced
            // column need quoting in anything generated from this FK.
            new ForeignKeyInfo(9002, "__MigrationHistory_user_id_fkey",
                ParentTableId: MigrationHistoryId, ParentOrdinals: new[] { 3 },
                ReferencedTableId: UsersId, ReferencedOrdinals: new[] { 1 }),
        };

        // search_path is public only; audit is visible in the catalog but not reachable unqualified.
        return new SchemaSnapshot("testdb", new[] { "public", "audit" }, tables, columns, fks,
            searchPath: new[] { "public" });
    }
}
