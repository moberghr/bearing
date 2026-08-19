using Bearing.Core.Schema;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Lookup behaviour of <see cref="SchemaSnapshot"/>, whose (schema, name) index is case-folded because
/// Postgres folds unquoted identifiers — while still allowing <c>Users</c> and <c>users</c> to coexist in
/// one schema behind quotes. Building that snapshot used to throw.
/// </summary>
public class SchemaSnapshotTests
{
    private const long UpperId = 10;
    private const long LowerId = 11;

    /// <summary>Two relations in one schema differing only by case, as quoted DDL can create them.</summary>
    private static SchemaSnapshot CaseColliding() => new(
        "testdb",
        new[] { "public" },
        new[]
        {
            new TableInfo(UpperId, "public", "Users", RelationKind.Table),
            new TableInfo(LowerId, "public", "users", RelationKind.Table),
        },
        new[]
        {
            new ColumnInfo(UpperId, 1, "Id", "int4", true, true),
            new ColumnInfo(LowerId, 1, "id", "int8", true, true),
        },
        Array.Empty<ForeignKeyInfo>());

    [Fact]
    public void Two_relations_differing_only_by_case_in_one_schema_do_not_break_the_snapshot()
    {
        var schema = CaseColliding();

        Assert.Equal(2, schema.Tables.Count);
        // Both survive as distinct relations, each keeping its own columns.
        Assert.Equal("Id", Assert.Single(schema.ColumnsOf(UpperId)).Name);
        Assert.Equal("id", Assert.Single(schema.ColumnsOf(LowerId)).Name);
    }

    [Fact]
    public void A_qualified_name_resolves_to_the_relation_spelled_exactly_that_way()
    {
        var schema = CaseColliding();

        Assert.Equal(UpperId, schema.ResolveTable("public", "Users")!.Id);
        Assert.Equal(LowerId, schema.ResolveTable("public", "users")!.Id);
    }

    [Fact]
    public void A_bare_name_resolves_to_the_relation_spelled_exactly_that_way()
    {
        var schema = CaseColliding();

        Assert.Equal(UpperId, schema.ResolveTable(null, "Users")!.Id);
        Assert.Equal(LowerId, schema.ResolveTable(null, "users")!.Id);
    }

    /// <summary>The folded lookup is the point of lower-casing: unquoted SQL must still find a mixed-case
    /// relation when no exact-case sibling exists.</summary>
    [Fact]
    public void An_unquoted_name_still_folds_onto_a_mixed_case_relation()
    {
        var schema = TestSchema.Build();

        var byFolded = schema.ResolveTable("public", "__migrationhistory");
        Assert.Equal(TestSchema.MigrationHistoryId, byFolded!.Id);
        Assert.Equal(TestSchema.MigrationHistoryId, schema.ResolveTable(null, "__MIGRATIONHISTORY")!.Id);
    }

    /// <summary>Exact case narrows the candidates; it does not replace search_path ordering when several
    /// schemas hold the same spelling.</summary>
    [Fact]
    public void An_ambiguous_bare_name_still_prefers_the_earliest_schema()
    {
        var schema = new SchemaSnapshot(
            "testdb",
            new[] { "public", "audit" },
            new[]
            {
                new TableInfo(20, "public", "events", RelationKind.Table),
                new TableInfo(21, "audit", "events", RelationKind.Table),
            },
            Array.Empty<ColumnInfo>(),
            Array.Empty<ForeignKeyInfo>());

        Assert.Equal(20, schema.ResolveTable(null, "events")!.Id);
        Assert.Equal(21, schema.ResolveTable("audit", "events")!.Id);
    }

    [Fact]
    public void An_unknown_name_resolves_to_null()
    {
        var schema = CaseColliding();

        Assert.Null(schema.ResolveTable("public", "orders"));
        Assert.Null(schema.ResolveTable("other", "users"));
        Assert.Null(schema.ResolveTable(null, "orders"));
    }
}
