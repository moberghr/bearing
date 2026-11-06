using Bearing.Core.Completion;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Completion read through <see cref="MySqlParseRules"/> — probes at real carets, kept as tests (§9.5). The
/// fixture is shaped like a MySQL database rather than a Postgres one: the database <em>is</em> the schema,
/// names keep their case, and the two awkward names need backticks (a space, and a reserved word).
/// </summary>
public class MySqlCompletionTests
{
    private const long CustomersId = 21;
    private const long OrdersId = 22;
    private const long OrderDetailsId = 23;
    private const long OrderId = 24;

    private static readonly SchemaSnapshot Schema = Build();

    private static CompletionEngine MySql => new(() => MySqlDialect.Instance);

    private static SchemaSnapshot Build()
    {
        var tables = new[]
        {
            new TableInfo(CustomersId, "shop", "Customers", RelationKind.Table),
            new TableInfo(OrdersId, "shop", "orders", RelationKind.Table),
            new TableInfo(OrderDetailsId, "shop", "order details", RelationKind.Table),
            new TableInfo(OrderId, "shop", "order", RelationKind.Table),
        };
        var columns = new[]
        {
            new ColumnInfo(CustomersId, 1, "id", "int", true, true),
            new ColumnInfo(CustomersId, 2, "name", "varchar", false, false),
            new ColumnInfo(OrdersId, 1, "id", "int", true, true),
            new ColumnInfo(OrdersId, 2, "customer_id", "int", true, false),
            new ColumnInfo(OrdersId, 3, "total", "decimal", false, false),
            new ColumnInfo(OrderDetailsId, 1, "order_id", "int", true, true),
            new ColumnInfo(OrderDetailsId, 2, "qty", "int", false, false),
            new ColumnInfo(OrderId, 1, "id", "int", true, true),
        };
        var fks = new[]
        {
            new ForeignKeyInfo(9201, "fk_orders_customers",
                ParentTableId: OrdersId, ParentOrdinals: new[] { 2 },
                ReferencedTableId: CustomersId, ReferencedOrdinals: new[] { 1 }),
        };
        return new SchemaSnapshot("shop", new[] { "shop" }, tables, columns, fks, searchPath: new[] { "shop" });
    }

    private static CompletionResult Complete(string sql) => MySql.Complete(sql, sql.Length, Schema);

    private static IReadOnlyList<string> Displays(CompletionResult r) => r.Suggestions.Select(s => s.DisplayText).ToList();

    private static string? Insertion(CompletionResult r, string display)
        => r.Suggestions.FirstOrDefault(s => s.DisplayText == display)?.ReplacementText;

    [Fact]
    public void From_offers_relations()
    {
        var r = Complete("select * from ");
        Assert.Contains("Customers", Displays(r));
        Assert.Contains("orders", Displays(r));
    }

    [Fact]
    public void Names_that_need_it_are_backticked_and_case_is_left_alone()
    {
        var r = Complete("select * from ");
        Assert.StartsWith("Customers", Insertion(r, "Customers"));
        Assert.StartsWith("`order details`", Insertion(r, "order details"));
        Assert.StartsWith("`order`", Insertion(r, "order"));
    }

    [Fact]
    public void A_predicate_offers_the_sources_columns_and_stays_short()
    {
        var r = Complete("select * from orders o where ");
        Assert.Contains("customer_id", Displays(r));
        Assert.Contains("total", Displays(r));
        // The container rule (`expression`) keeps c3 from enumerating every token that could follow.
        Assert.True(r.Suggestions.Count < 40, $"{r.Suggestions.Count} suggestions");
    }

    [Fact]
    public void A_qualified_column_resolves_through_a_backticked_alias_source()
    {
        var r = Complete("select * from `order details` d where d.");
        Assert.Contains("qty", Displays(r));
        Assert.DoesNotContain("total", Displays(r));
    }

    [Fact]
    public void A_schema_qualified_source_resolves()
    {
        var r = Complete("select * from shop.orders o where o.");
        Assert.Contains("total", Displays(r));
    }

    [Fact]
    public void Update_set_offers_only_the_targets_columns()
    {
        var r = Complete("update orders set ");
        Assert.Contains("total", Displays(r));
        Assert.DoesNotContain("name", Displays(r));
    }

    [Fact]
    public void Insert_into_offers_relations_without_an_alias()
    {
        var r = Complete("insert into ");
        Assert.Equal("orders", Insertion(r, "orders"));
    }

    [Fact]
    public void A_caret_inside_a_backslash_escaped_string_gets_nothing()
        => Assert.Empty(Complete("select 'it\\'s ").Suggestions);

    [Fact]
    public void A_caret_after_a_backslash_escaped_string_still_completes()
        => Assert.Contains("orders", Displays(Complete("select 'it\\'s' from ")));
}
