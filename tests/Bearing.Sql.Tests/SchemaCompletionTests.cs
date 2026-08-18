using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Schemas were absent from completion in both directions: they were never offered at a table
/// position, and typing <c>public.</c> produced an empty popup because any <c>ident.</c> was routed to
/// the column path. A relation outside search_path also has to be inserted schema-qualified.
/// </summary>
public class SchemaCompletionTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    private static CompletionResult At(string sql) => Engine.Complete(sql, sql.Length, Schema);

    private static string[] Kinds(string sql, SuggestionKind kind)
        => At(sql).Suggestions.Where(s => s.Kind == kind).Select(s => s.DisplayText).OrderBy(x => x).ToArray();

    [Fact]
    public void A_table_position_offers_schema_names()
        => Assert.Equal(new[] { "audit", "public" }, Kinds("select * from ", SuggestionKind.Schema));

    [Fact]
    public void A_schema_inserts_with_its_trailing_dot_so_the_relation_list_continues()
    {
        var audit = At("select * from ").Suggestions.Single(s => s.Kind == SuggestionKind.Schema && s.DisplayText == "audit");
        Assert.Equal("audit.", audit.ReplacementText);
        Assert.Equal("audit", audit.FilterText);      // no dot in the label or the filter
    }

    [Fact]
    public void A_schema_qualifier_lists_that_schemas_relations()
    {
        var tables = Kinds("select * from audit.", SuggestionKind.Table);
        Assert.Equal(new[] { "events" }, tables);
        // …and nothing from the other schema
        Assert.DoesNotContain("users", tables);
    }

    [Fact]
    public void Under_a_schema_qualifier_the_relation_is_inserted_bare()
    {
        var events = At("select * from audit.").Suggestions.Single(s => s.Kind == SuggestionKind.Table);
        Assert.Equal("events e", events.ReplacementText);   // the schema is already typed
    }

    [Fact]
    public void An_in_scope_alias_still_wins_over_a_same_named_schema()
    {
        // "public" as an alias: columns, not the schema's relations.
        var result = At("select * from users public where public.");
        Assert.Contains(result.Suggestions, s => s.Kind == SuggestionKind.Column && s.DisplayText == "email");
        Assert.DoesNotContain(result.Suggestions, s => s.Kind == SuggestionKind.Table);
    }

    [Fact]
    public void An_unknown_qualifier_still_offers_nothing()
        => Assert.Empty(At("select * from users u where nosuchthing.").Suggestions);

    [Fact]
    public void A_relation_outside_search_path_is_inserted_schema_qualified()
    {
        var events = At("select * from ").Suggestions
            .Single(s => s.Kind == SuggestionKind.Table && s.DisplayText == "events");
        Assert.Equal("audit.events e", events.ReplacementText);
        Assert.Equal("events", events.DisplayText);      // the label stays bare
        Assert.Equal("audit", events.DetailText);
    }

    [Fact]
    public void A_relation_on_the_search_path_stays_unqualified()
        => Assert.Equal("users u", At("select * from ").Suggestions
            .Single(s => s.Kind == SuggestionKind.Table && s.DisplayText == "users").ReplacementText);
}
