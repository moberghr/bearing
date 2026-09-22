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
public class SchemaCatalogTests
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
        var json = SchemaCatalog.Tables(Pagila(), schema: null);

        Assert.Equal(3, json.Tables.Count);
        Assert.Equal(3, json.TableCount);
        Assert.Null(json.Truncated);

        var view = Assert.Single(json.Tables, t => t.Name == "monthly");
        Assert.Equal("reporting", view.Schema);
        Assert.Equal("view", view.Kind);
    }

    [Fact]
    public void A_schema_filter_narrows_the_listing_and_ignores_case()
    {
        var json = SchemaCatalog.Tables(Pagila(), schema: "REPORTING");

        Assert.Equal("monthly", Assert.Single(json.Tables).Name);
    }

    /// <summary>
    /// A listing silently cut at the cap reads exactly like a database with that many relations, and a
    /// model would conclude the table it wants does not exist. The count is of what matched, not of what
    /// was returned, so the two together say what happened.
    /// </summary>
    [Fact]
    public void A_listing_longer_than_the_cap_says_so_rather_than_stopping_quietly()
    {
        var many = Enumerable.Range(1, SchemaCatalog.MaxTables + 20)
            .Select(i => new TableInfo(i, "public", $"t{i:0000}", RelationKind.Table))
            .ToList();
        var snapshot = new SchemaSnapshot("app", ["public"], many, [], []);

        var json = SchemaCatalog.Tables(snapshot, schema: null);

        Assert.Equal(SchemaCatalog.MaxTables, json.Tables.Count);
        Assert.Equal(SchemaCatalog.MaxTables + 20, json.TableCount);
        Assert.True(json.Truncated);
    }

    [Fact]
    public void A_table_reports_its_columns_with_nullability_and_the_primary_key()
    {
        var snapshot = Pagila();

        var json = SchemaCatalog.Table(snapshot, snapshot.TableById(Payment)!);

        Assert.Equal(["payment_id", "customer_id", "amount"], json.Columns.Select(c => c.Name));
        Assert.True(json.Columns[0].PrimaryKey);
        Assert.False(json.Columns[2].PrimaryKey);
        Assert.True(json.Columns[1].NotNull);
        Assert.False(json.Columns[2].NotNull);
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

        var declaring = SchemaCatalog.Table(snapshot, snapshot.TableById(Payment)!).ForeignKeys;
        var referenced = SchemaCatalog.Table(snapshot, snapshot.TableById(Customer)!).ForeignKeys;

        foreach (var side in new[] { declaring, referenced })
        {
            var fk = Assert.Single(side);
            Assert.Equal("payment_customer_id_fkey", fk.Name);
            Assert.Equal("public.payment", fk.From.Table);
            Assert.Equal("customer_id", fk.From.Columns[0]);
            Assert.Equal("public.customer", fk.To.Table);
            Assert.Equal("customer_id", fk.To.Columns[0]);
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
