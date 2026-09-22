using System.Text.Json.Nodes;
using Bearing.Core.Schema;
using Bearing.Cli.Tools;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// The catalog on the way out, over a real <see cref="SchemaSnapshot"/> rather than a fake one — the
/// resolution rules (search path, casing) are Core's and asserting them against a stand-in would be
/// asserting our assumptions back (§4.6).
/// </summary>
public class SchemaJsonTests
{
    private const long Payment = 1;
    private const long Customer = 2;
    private const long Report = 3;

    private static SchemaSnapshot Pagila() => new(
        database: "app",
        schemas: ["public", "reporting"],
        tables:
        [
            new TableInfo(Payment, "public", "payment", RelationKind.Table),
            new TableInfo(Customer, "public", "customer", RelationKind.Table),
            new TableInfo(Report, "reporting", "monthly", RelationKind.View),
        ],
        columns:
        [
            new ColumnInfo(Payment, 1, "payment_id", "integer", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(Payment, 2, "customer_id", "integer", NotNull: true, IsPrimaryKey: false),
            new ColumnInfo(Payment, 3, "amount", "numeric", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(Customer, 1, "customer_id", "integer", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(Report, 1, "total", "numeric", NotNull: false, IsPrimaryKey: false),
        ],
        foreignKeys:
        [
            new ForeignKeyInfo(10, "payment_customer_id_fkey", Payment, [2], Customer, [1]),
        ]);

    [Fact]
    public void A_listing_names_every_relation_with_its_kind()
    {
        var json = SchemaJson.Tables(Pagila(), schema: null);

        var tables = (JsonArray)json["tables"]!;
        Assert.Equal(3, tables.Count);
        Assert.Equal(3, json["table_count"]!.GetValue<int>());
        Assert.Null(json["truncated"]);

        var view = tables.Single(t => t!["name"]!.GetValue<string>() == "monthly")!;
        Assert.Equal("reporting", view["schema"]!.GetValue<string>());
        Assert.Equal("view", view["kind"]!.GetValue<string>());
    }

    [Fact]
    public void A_schema_filter_narrows_the_listing_and_ignores_case()
    {
        var json = SchemaJson.Tables(Pagila(), schema: "REPORTING");

        var tables = (JsonArray)json["tables"]!;
        Assert.Equal("monthly", Assert.Single(tables)!["name"]!.GetValue<string>());
    }

    /// <summary>
    /// A listing silently cut at the cap reads exactly like a database with that many relations, and a
    /// model would conclude the table it wants does not exist. The count is of what matched, not of what
    /// was returned, so the two together say what happened.
    /// </summary>
    [Fact]
    public void A_listing_longer_than_the_cap_says_so_rather_than_stopping_quietly()
    {
        var many = Enumerable.Range(1, SchemaJson.MaxTables + 20)
            .Select(i => new TableInfo(i, "public", $"t{i:0000}", RelationKind.Table))
            .ToList();
        var snapshot = new SchemaSnapshot("app", ["public"], many, [], []);

        var json = SchemaJson.Tables(snapshot, schema: null);

        Assert.Equal(SchemaJson.MaxTables, ((JsonArray)json["tables"]!).Count);
        Assert.Equal(SchemaJson.MaxTables + 20, json["table_count"]!.GetValue<int>());
        Assert.True(json["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void A_table_reports_its_columns_with_nullability_and_the_primary_key()
    {
        var snapshot = Pagila();

        var json = SchemaJson.Table(snapshot, snapshot.TableById(Payment)!);

        var columns = (JsonArray)json["columns"]!;
        Assert.Equal(["payment_id", "customer_id", "amount"], columns.Select(c => c!["name"]!.GetValue<string>()));
        Assert.True(columns[0]!["primary_key"]!.GetValue<bool>());
        Assert.False(columns[2]!["primary_key"]!.GetValue<bool>());
        Assert.True(columns[1]!["not_null"]!.GetValue<bool>());
        Assert.False(columns[2]!["not_null"]!.GetValue<bool>());
    }

    /// <summary>
    /// "Which tables reference customer" is the question asked before writing a join, and it cannot be
    /// answered from the declaring side alone — so a key is reported on both relations it touches, with
    /// both ends named in full.
    /// </summary>
    [Fact]
    public void A_key_is_reported_on_the_table_it_points_at_as_well_as_the_one_declaring_it()
    {
        var snapshot = Pagila();

        var declaring = (JsonArray)SchemaJson.Table(snapshot, snapshot.TableById(Payment)!)["foreign_keys"]!;
        var referenced = (JsonArray)SchemaJson.Table(snapshot, snapshot.TableById(Customer)!)["foreign_keys"]!;

        foreach (var side in new[] { declaring, referenced })
        {
            var fk = Assert.Single(side)!;
            Assert.Equal("payment_customer_id_fkey", fk["name"]!.GetValue<string>());
            Assert.Equal("public.payment", fk["from"]!["table"]!.GetValue<string>());
            Assert.Equal("customer_id", fk["from"]!["columns"]![0]!.GetValue<string>());
            Assert.Equal("public.customer", fk["to"]!["table"]!.GetValue<string>());
            Assert.Equal("customer_id", fk["to"]!["columns"]![0]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("payment", null, "payment")]
    [InlineData("public.payment", "public", "payment")]
    [InlineData("\"public\".\"payment\"", "public", "payment")]
    [InlineData("  payment  ", null, "payment")]
    // A trailing dot is not a qualification, and treating it as one would look for a table with no name.
    [InlineData("payment.", null, "payment.")]
    public void A_table_name_is_split_on_its_qualifier_only_when_it_has_one(
        string given, string? schema, string name)
    {
        var (gotSchema, gotName) = BearingHost.SplitQualified(given);

        Assert.Equal(schema, gotSchema);
        Assert.Equal(name, gotName);
    }
}
