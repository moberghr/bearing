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
    public void Carries_the_catalogs_not_null_flag_so_the_grid_can_refuse_to_offer_null()
    {
        // The grid's checkbox column reads this: a NOT NULL bool must not offer its indeterminate state.
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            new ColumnDescriptor("user_id", "int4", typeof(int), TestSchema.OrdersId, 2),
            new ColumnDescriptor("total", "numeric", typeof(decimal), TestSchema.OrdersId, 3),
        };

        var t = EditabilityResolver.Resolve(Schema, cols);
        Assert.NotNull(t);
        Assert.True(t!.Columns[1].NotNull);      // orders.user_id is NOT NULL
        Assert.False(t.Columns[2].NotNull);      // orders.total is nullable

        Assert.False(t.AllowsNull(1));
        Assert.True(t.AllowsNull(2));
        Assert.True(t.AllowsNull(99));           // unmapped column: don't-know must not forbid a legal value
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

/// <summary>
/// The same resolver over the name-based column origin — what a SqlDataReader hands back, since it
/// exposes base schema/table/column names and no catalog ids at all. The schema is the same hand-built
/// one; only the way the columns say where they came from changes.
/// </summary>
public class EditabilityResolverNameOriginTests
{
    private static readonly ISchemaSnapshot Schema = TestSchema.Build();

    private static ColumnDescriptor Named(string alias, string? schema, string table, string? column)
        => new(alias, "int", typeof(int), BaseSchemaName: schema, BaseTableName: table, BaseColumnName: column);

    private static ColumnDescriptor InCatalog(string alias, string catalog, string schema, string table, string column)
        => new(alias, "int", typeof(int), BaseSchemaName: schema, BaseTableName: table, BaseColumnName: column,
               BaseCatalogName: catalog);

    [Fact]
    public void Single_table_resolved_by_name_is_editable()
    {
        var cols = new[]
        {
            Named("id", "public", "orders", "id"),
            Named("user_id", "public", "orders", "user_id"),
            Named("total", "public", "orders", "total"),
        };

        var t = EditabilityResolver.Resolve(Schema, cols);
        Assert.NotNull(t);
        Assert.Equal("public", t!.Schema);
        Assert.Equal("orders", t.Table);
        Assert.Equal(new[] { "id" }, t.KeyColumns.Select(k => k.BaseColumn));
        Assert.True(t.Columns[1].NotNull);      // orders.user_id — the catalog flag still arrives
        Assert.False(t.AllowsNull(1));
    }

    [Fact]
    public void Name_lookup_is_case_insensitive()
    {
        // SQL Server's default collation is case-insensitive, so KeyInfo can report `Orders`.`Total`
        // for what the snapshot holds as public.orders.total.
        var cols = new[]
        {
            Named("Id", "PUBLIC", "Orders", "ID"),
            Named("Total", "PUBLIC", "Orders", "Total"),
        };

        var t = EditabilityResolver.Resolve(Schema, cols);
        Assert.NotNull(t);
        Assert.Equal("orders", t!.Table);
        Assert.Equal("id", t.Columns[0].BaseColumn);   // catalog spelling, not the reported one
        Assert.True(t.Columns[0].IsPrimaryKey);
    }

    [Fact]
    public void Uses_the_catalog_ordinal_so_the_target_matches_the_id_path_exactly()
    {
        var byName = EditabilityResolver.Resolve(Schema, new[]
        {
            Named("id", "public", "orders", "id"),
            Named("user_id", "public", "orders", "user_id"),
            Named("total", "public", "orders", "total"),
        });
        var byId = EditabilityResolver.Resolve(Schema, new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            new ColumnDescriptor("user_id", "int4", typeof(int), TestSchema.OrdersId, 2),
            new ColumnDescriptor("total", "numeric", typeof(decimal), TestSchema.OrdersId, 3),
        });

        Assert.NotNull(byName);
        Assert.NotNull(byId);
        Assert.Equal(byId!.Schema, byName!.Schema);
        Assert.Equal(byId.Table, byName.Table);
        // Element-wise: EditTarget is a record but Columns is a list, so record equality would compare
        // it by reference and pass for any two targets.
        Assert.Equal(byId.Columns, byName.Columns);
    }

    [Fact]
    public void Unqualified_table_name_resolves_through_the_search_path()
    {
        // KeyInfo can leave the schema null; the snapshot's own bare-name rules decide, as they do for
        // completion.
        var cols = new[] { Named("id", null, "orders", "id"), Named("total", null, "orders", "total") };

        var t = EditabilityResolver.Resolve(Schema, cols);
        Assert.NotNull(t);
        Assert.Equal("public", t!.Schema);
    }

    [Fact]
    public void Column_with_no_base_column_name_is_a_computed_expression()
    {
        // An expression under KeyInfo reports no base column (and often no base table either). The table
        // alone is not enough to edit through, so it must read as an expression, not a half-mapped column.
        var cols = new[]
        {
            Named("id", "public", "orders", "id"),
            Named("doubled", "public", "orders", null),
        };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("expression", reason);
    }

    [Fact]
    public void Names_from_two_tables_report_the_join_reason()
    {
        var cols = new[] { Named("id", "public", "orders", "id"), Named("name", "public", "users", "name") };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("join", reason);
    }

    [Fact]
    public void Same_name_in_two_schemas_reports_the_join_reason()
    {
        var cols = new[] { Named("id", "public", "orders", "id"), Named("id", "audit", "orders", "id") };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("join", reason);
    }

    [Fact]
    public void Unknown_table_name_reports_the_missing_schema_reason()
    {
        var cols = new[] { Named("id", "public", "nowhere", "id") };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("loaded schema", reason);
    }

    [Fact]
    public void Unknown_column_name_reports_the_missing_column_reason()
    {
        var cols = new[] { Named("id", "public", "orders", "id"), Named("ghost", "public", "orders", "ghost") };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("a column isn't in the loaded schema.", reason);
    }

    [Fact]
    public void Missing_primary_key_by_name_is_not_editable()
    {
        var cols = new[] { Named("name", "public", "users", "name"), Named("email", "public", "users", "email") };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("primary-key", reason);
    }

    [Fact]
    public void Mixed_origins_pointing_at_one_table_still_resolve()
    {
        // Not a shape either provider emits today, but the resolver must not treat "two forms" as
        // "two tables" — it resolves both to a relation first and only then compares.
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            Named("total", "public", "orders", "total"),
        };

        var t = EditabilityResolver.Resolve(Schema, cols);
        Assert.NotNull(t);
        Assert.Equal("orders", t!.Table);
        Assert.Equal("total", t.Columns[1].BaseColumn);
    }

    [Fact]
    public void Mixed_origins_pointing_at_different_tables_report_the_join_reason()
    {
        var cols = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), TestSchema.OrdersId, 1),
            Named("name", "public", "users", "name"),
        };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Contains("join", reason);
    }

    // ---- The catalog qualifies the name ----------------------------------------------------------

    [Fact]
    public void A_table_in_another_database_is_not_editable()
    {
        // T-SQL reaches another database with a three-part name; Postgres has no analogue. The snapshot
        // describes "testdb" only, so a name origin from "reporting" must NOT resolve to the local table
        // of the same name — that generated an UPDATE against the connected database while the confirm
        // dialog showed an identical [public].[orders], which is the one shape §1.2's guard cannot expose.
        var cols = new[]
        {
            InCatalog("id", "reporting", "public", "orders", "id"),
            InCatalog("total", "reporting", "public", "orders", "total"),
        };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Equal("the source table is in another database.", reason);
    }

    [Fact]
    public void The_connected_database_resolves_normally_whatever_its_case()
    {
        // The catalog check must not break the ordinary case, and SQL Server's collation is case-insensitive.
        var cols = new[]
        {
            InCatalog("id", "TESTDB", "public", "orders", "id"),
            InCatalog("total", "testdb", "public", "orders", "total"),
        };

        Assert.NotNull(EditabilityResolver.Resolve(Schema, cols));
    }

    [Fact]
    public void A_result_spanning_two_databases_reads_as_a_join()
    {
        // Same table and schema name, different database: two tables however it is spelled.
        var cols = new[]
        {
            InCatalog("id", "testdb", "public", "orders", "id"),
            InCatalog("total", "reporting", "public", "orders", "total"),
        };

        var (target, reason) = EditabilityResolver.ResolveWithReason(Schema, cols);
        Assert.Null(target);
        Assert.Equal("the result joins more than one table.", reason);
    }

    [Fact]
    public void A_name_origin_with_no_catalog_still_resolves()
    {
        // Postgres never sets it, and KeyInfo can leave it null. Absent must stay permissive, or every
        // Postgres result would become read-only.
        Assert.NotNull(EditabilityResolver.Resolve(Schema, new[]
        {
            Named("id", "public", "orders", "id"),
            Named("total", "public", "orders", "total"),
        }));
    }
}
