using System.Collections.Generic;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;
using Xunit;

namespace Squirrel.Sql.Tests;

/// <summary>Resolver over the hand-built users(1) &lt;- orders(2) schema (orders.user_id → users.id).</summary>
public class ForeignKeyResolverTests
{
    private static readonly ISchemaSnapshot Schema = TestSchema.Build();

    // Columns as `select * from orders` would produce them, with catalog origin (oid + attnum) set.
    private static IReadOnlyList<ColumnDescriptor> OrdersColumns() => new[]
    {
        new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersOid, 1),
        new ColumnDescriptor("user_id", "int4", typeof(int), TestSchema.OrdersOid, 2),
        new ColumnDescriptor("total", "numeric", typeof(decimal), TestSchema.OrdersOid, 3),
    };

    [Fact]
    public void Referencing_column_resolves_to_the_referenced_table()
    {
        var target = ForeignKeyResolver.Resolve(Schema, OrdersColumns(), clickedColumn: 1);

        Assert.NotNull(target);
        Assert.Equal("public", target!.RefSchema);
        Assert.Equal("users", target.RefTable);
        Assert.Equal(new[] { "id" }, target.RefColumns);
        Assert.Equal(new[] { 1 }, target.SourceColumnIndices); // user_id is result column 1
    }

    [Fact]
    public void Non_fk_columns_do_not_resolve()
    {
        var cols = OrdersColumns();
        Assert.Null(ForeignKeyResolver.Resolve(Schema, cols, 0)); // orders.id (PK, not a referencing FK)
        Assert.Null(ForeignKeyResolver.Resolve(Schema, cols, 2)); // orders.total (plain column)
    }

    [Fact]
    public void Referenced_side_is_not_navigable()
    {
        // users.id is the referenced (child) side of the FK — clicking it should not navigate.
        var usersCols = new[] { new ColumnDescriptor("id", "int4", typeof(int), TestSchema.UsersOid, 1) };
        Assert.Null(ForeignKeyResolver.Resolve(Schema, usersCols, 0));
    }

    [Fact]
    public void Column_without_base_origin_does_not_resolve()
    {
        // An expression/aliased column (e.g. count(*)) carries no base table.
        var cols = new[] { new ColumnDescriptor("n", "int8", typeof(long)) };
        Assert.Null(ForeignKeyResolver.Resolve(Schema, cols, 0));
    }
}
