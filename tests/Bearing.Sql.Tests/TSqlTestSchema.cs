using Bearing.Core.Schema;

namespace Bearing.Sql.Tests;

/// <summary>
/// A hand-built SQL Server catalog for the T-SQL completion tests — the twin of
/// <see cref="TestSchema"/>, and spelled the way a real SQL Server database is rather than the way the
/// Postgres fixture is. That is the whole point of it: every name here is PascalCase, which is T-SQL's
/// convention and the case Postgres' quoting rule got wrong for <em>every</em> identifier.
/// <para>
/// The three shapes that matter:
/// <list type="bullet">
/// <item><c>dbo.Customers</c> — an ordinary PascalCase name, which must be inserted bare;</item>
/// <item><c>dbo.[Order Details]</c> — Northwind's own table, whose space forces brackets on the way out
/// and whose <c>SQUARE_BRACKET_ID</c> token has to be read as one name on the way in;</item>
/// <item><c>dbo.[Order]</c> — a reserved keyword, which needs brackets even though its case is fine, so
/// "needs quoting" cannot collapse into "has a space".</item>
/// </list>
/// </para>
/// </summary>
internal static class TSqlTestSchema
{
    public const long CustomersId = 11;
    public const long OrdersId = 12;
    public const long OrderDetailsId = 13;
    public const long OrderId = 14;

    public static SchemaSnapshot Build()
    {
        var tables = new[]
        {
            new TableInfo(CustomersId, "dbo", "Customers", RelationKind.Table),
            new TableInfo(OrdersId, "dbo", "Orders", RelationKind.Table),
            new TableInfo(OrderDetailsId, "dbo", "Order Details", RelationKind.Table),
            new TableInfo(OrderId, "dbo", "Order", RelationKind.Table),
            new TableInfo(99, "sales", "Regions", RelationKind.View),
        };

        var columns = new[]
        {
            new ColumnInfo(CustomersId, 1, "CustomerId", "int", true, true),
            new ColumnInfo(CustomersId, 2, "CompanyName", "nvarchar", false, false),
            new ColumnInfo(OrdersId, 1, "OrderId", "int", true, true),
            new ColumnInfo(OrdersId, 2, "CustomerId", "int", true, false),
            new ColumnInfo(OrdersId, 3, "Freight", "money", false, false),
            new ColumnInfo(OrderDetailsId, 1, "OrderId", "int", true, true),
            new ColumnInfo(OrderDetailsId, 2, "ProductId", "int", true, true),
            new ColumnInfo(OrderDetailsId, 3, "Quantity", "smallint", false, false),
            new ColumnInfo(OrderId, 1, "OrderId", "int", true, true),
            new ColumnInfo(99, 1, "RegionId", "int", true, true),
        };

        var fks = new[]
        {
            new ForeignKeyInfo(9101, "FK_Orders_Customers",
                ParentTableId: OrdersId, ParentOrdinals: new[] { 2 },
                ReferencedTableId: CustomersId, ReferencedOrdinals: new[] { 1 }),
            // The referencing side is the bracketed name, so anything generated from this FK has to
            // bracket a table name whose case is otherwise unremarkable.
            new ForeignKeyInfo(9102, "FK_OrderDetails_Orders",
                ParentTableId: OrderDetailsId, ParentOrdinals: new[] { 1 },
                ReferencedTableId: OrdersId, ReferencedOrdinals: new[] { 1 }),
        };

        // `dbo` is the default schema, so it is what resolves an unqualified name; `sales` is in the
        // catalog but reachable only qualified.
        return new SchemaSnapshot("Northwind", new[] { "dbo", "sales" }, tables, columns, fks,
            searchPath: new[] { "dbo" });
    }
}
