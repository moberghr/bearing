using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Workspace;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The decisions behind the two tree shapes (#132), with no tree in sight: which kinds belong to a schema and
/// which to the database, what order a schema reads in, which schemas exist at all, and how a count renders
/// when there is nothing to count.
/// </summary>
public class SchemaTreeShapeTests
{
    private static SchemaObjectInfo Obj(long id, string schema, string name)
        => new(id, schema, name, "detail");

    // ---- where a kind goes -----------------------------------------------------------------------

    [Fact]
    public void Every_kind_the_reader_returns_is_placed()
    {
        // The guard against adding a kind to the read and forgetting the tree: thirteen lists come back, and
        // thirteen rows have to know where they go.
        Assert.Equal(13, SchemaTreeShape.Kinds.Count);
        Assert.Equal(SchemaTreeShape.Kinds.Count,
            SchemaTreeShape.Kinds.Select(k => k.Title).Distinct().Count());
    }

    [Fact]
    public void A_kind_with_a_schema_sits_in_the_schema_and_the_rest_sit_in_Administer()
    {
        // The split is not invented — it follows whether the catalog gives the object a schema at all.
        string[] perSchema = ["Sequences", "Types", "Policies", "Collations", "Operators",
            "Operator classes", "Text search"];
        string[] administer = ["Extensions", "Event triggers", "Publications", "Subscriptions",
            "Foreign servers", "Casts"];

        foreach (var title in perSchema)
            Assert.Equal(SchemaKindPlacement.PerSchema,
                SchemaTreeShape.Kinds.Single(k => k.Title == title).Placement);

        foreach (var title in administer)
            Assert.Equal(SchemaKindPlacement.Administer,
                SchemaTreeShape.Kinds.Single(k => k.Title == title).Placement);
    }

    [Fact]
    public void A_schema_reads_from_what_a_query_starts_with_to_the_machinery()
    {
        // Tables lead because they are what nearly every expand is for — the one thing full mode must not
        // bury deeper than it already does.
        Assert.Equal("Tables", SchemaTreeShape.SchemaGroupTitles[0]);
        Assert.Equal(["Tables", "Views", "Functions", "Procedures"],
            SchemaTreeShape.SchemaGroupTitles.Take(4));

        // And every per-schema kind follows, none of the database-wide ones.
        Assert.DoesNotContain("Extensions", SchemaTreeShape.SchemaGroupTitles);
        Assert.Contains("Sequences", SchemaTreeShape.SchemaGroupTitles);
    }

    // ---- counts ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "—")]
    [InlineData(-1, "—")]
    [InlineData(1, "1")]
    [InlineData(42, "42")]
    public void A_count_of_nothing_is_a_dash_rather_than_a_zero(int count, string expected)
        // A zero reads as a measurement; the thing being said is that the kind is absent. A group whose read
        // has not landed is never built, so the mark can only mean "asked, and there are none" (§1.7).
        => Assert.Equal(expected, SchemaTreeShape.CountText(count));

    // ---- which schemas exist ---------------------------------------------------------------------

    private static ISchemaSnapshot Snapshot(params (string Schema, string Name)[] tables)
        => new SchemaSnapshot("db",
            tables.Select(t => t.Schema).Distinct().ToArray(),
            tables.Select((t, i) => new TableInfo(i + 1, t.Schema, t.Name, RelationKind.Table)).ToArray(),
            [], [], searchPath: ["public"]);

    [Fact]
    public void The_default_schema_leads_and_the_rest_follow_by_name()
    {
        var schemas = SchemaTreeShape.SchemasOf(
            Snapshot(("zeta", "a"), ("public", "b"), ("audit", "c")), [], kinds: null, "public");

        Assert.Equal(["public", "audit", "zeta"], schemas);
    }

    [Fact]
    public void A_schema_holding_only_a_sequence_still_exists()
    {
        // It would be invisible if this counted tables and routines alone, which is what it did while the
        // schema level was merely additive (§9.9a). In full mode that schema is unreachable.
        var kinds = DatabaseObjectKinds.Empty with
        {
            Sequences = [new SequenceInfo(1, "gen", "seq", "bigint", null, 1, 1, 1, false, null, null)],
        };

        var without = SchemaTreeShape.SchemasOf(Snapshot(("public", "a")), [], kinds: null, "public");
        var with = SchemaTreeShape.SchemasOf(Snapshot(("public", "a")), [], kinds, "public");

        Assert.DoesNotContain("gen", without);
        Assert.Contains("gen", with);
    }

    [Fact]
    public void A_kind_with_no_schema_of_its_own_does_not_invent_one()
    {
        // A cast's schema is empty by design (SchemaObjectInfo), and an empty string is not a schema name.
        var kinds = DatabaseObjectKinds.Empty with { Casts = [Obj(1, "", "int4 → text")] };

        var schemas = SchemaTreeShape.SchemasOf(Snapshot(("public", "a")), [], kinds, "public");

        Assert.Equal(["public"], schemas);
    }

    // ---- the long tail ---------------------------------------------------------------------------

    [Fact]
    public void The_bucket_counts_what_is_inside_it_rather_than_its_groups()
    {
        // It stands for the objects, not for the thirteen rows it is made of: "Other objects 14" is the
        // number that tells you whether opening it is worth it.
        var kinds = DatabaseObjectKinds.Empty with
        {
            Casts = [Obj(1, "", "a"), Obj(2, "", "b")],
            Publications = [Obj(3, "", "p")],
        };

        Assert.Equal(3, SchemaTreeShape.LongTailCount(kinds));
        Assert.Equal(0, SchemaTreeShape.LongTailCount(DatabaseObjectKinds.Empty));
    }
}
