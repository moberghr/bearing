using System.Linq;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>Editability over the hand-built users(1, pk id) / orders(2, pk id) schema.</summary>
public class EditabilityResolverTests
{
    private static readonly ISchemaSnapshot Schema = TestSchema.Build();

    [Fact]
    public void Single_table_with_pk_present_is_editable()
    {
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            new ColumnDescriptor("user_id", "int4", typeof(int), TestSchema.OrdersId, 2),
            new ColumnDescriptor("total", "numeric", typeof(decimal), TestSchema.OrdersId, 3),
        };

        var t = EditabilityResolver.Resolve(Schema, cols);
        Assert.NotNull(t);
        Assert.Equal("public", t!.Schema);
        Assert.Equal("orders", t.Table);
        Assert.Equal(new[] { "id" }, t.KeyColumns.Select(k => k.BaseColumn));
        Assert.False(t.Columns[1].IsPrimaryKey);
    }

    [Fact]
    public void Uses_catalog_column_name_not_result_alias()
    {
        // `select id as oid, name, email from users` — PK still maps to catalog name "id".
        var cols = new[]
        {
            new ColumnDescriptor("oid", "int4", typeof(int), TestSchema.UsersId, 1),
            new ColumnDescriptor("name", "text", typeof(string), TestSchema.UsersId, 2),
            new ColumnDescriptor("email", "text", typeof(string), TestSchema.UsersId, 3),
        };

        var t = EditabilityResolver.Resolve(Schema, cols);
        Assert.NotNull(t);
        Assert.Equal("id", t!.Columns[0].BaseColumn);   // not "oid"
        Assert.True(t.Columns[0].IsPrimaryKey);
    }

    [Fact]
    public void Missing_primary_key_is_not_editable()
    {
        // users without its PK column (id) present.
        var cols = new[]
        {
            new ColumnDescriptor("name", "text", typeof(string), TestSchema.UsersId, 2),
            new ColumnDescriptor("email", "text", typeof(string), TestSchema.UsersId, 3),
        };
        Assert.Null(EditabilityResolver.Resolve(Schema, cols));
    }

    [Fact]
    public void Columns_from_multiple_tables_are_not_editable()
    {
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            new ColumnDescriptor("name", "text", typeof(string), TestSchema.UsersId, 2),
        };
        Assert.Null(EditabilityResolver.Resolve(Schema, cols));
    }

    [Fact]
    public void Expression_column_makes_it_not_editable()
    {
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            new ColumnDescriptor("doubled", "numeric", typeof(decimal)), // no base origin
        };
        Assert.Null(EditabilityResolver.Resolve(Schema, cols));
    }

    [Fact]
    public void Reason_is_null_when_editable()
    {
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            new ColumnDescriptor("total", "numeric", typeof(decimal), TestSchema.OrdersId, 3),
        };
        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.NotNull(target);
        Assert.Null(reason);
    }

    [Fact]
    public void Reason_reports_missing_primary_key()
    {
        var cols = new[]
        {
            new ColumnDescriptor("name", "text", typeof(string), TestSchema.UsersId, 2),
            new ColumnDescriptor("email", "text", typeof(string), TestSchema.UsersId, 3),
        };
        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("primary-key", reason);
    }

    [Fact]
    public void Reason_reports_join_across_tables()
    {
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            new ColumnDescriptor("name", "text", typeof(string), TestSchema.UsersId, 2),
        };
        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("join", reason);
    }

    [Fact]
    public void Reason_reports_computed_expression()
    {
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            new ColumnDescriptor("doubled", "numeric", typeof(decimal)), // no base origin
        };
        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("expression", reason);
    }
}
