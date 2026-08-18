using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Completion has to insert SQL that runs: Postgres folds unquoted identifiers to lower case, so a
/// mixed-case relation (<c>__MigrationHistory</c>) and a reserved-word one (<c>order</c>) must come
/// back quoted — while ordinary lower-case names stay bare, because the inserted text is read and
/// typed over.
/// </summary>
public class QuotedIdentifierCompletionTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    private static CompletionResult At(string sql) => Engine.Complete(sql, sql.Length, Schema);

    private static string Replacement(string sql, string displayText, SuggestionKind kind)
    {
        var hit = At(sql).Suggestions.FirstOrDefault(s => s.Kind == kind && s.DisplayText == displayText);
        Assert.NotNull(hit);
        return hit!.ReplacementText;
    }

    [Fact]
    public void A_mixed_case_relation_is_inserted_quoted_and_aliased()
        => Assert.Equal("\"__MigrationHistory\" mh",
            Replacement("select * from ", "__MigrationHistory", SuggestionKind.Table));

    [Fact]
    public void A_reserved_word_relation_is_inserted_quoted()
        => Assert.Equal("\"order\" o", Replacement("select * from ", "order", SuggestionKind.Table));

    [Fact]
    public void An_ordinary_relation_stays_bare()
        => Assert.Equal("users u", Replacement("select * from ", "users", SuggestionKind.Table));

    [Fact]
    public void The_list_still_shows_and_filters_on_the_bare_name()
    {
        var hit = At("select * from ").Suggestions.Single(
            s => s.Kind == SuggestionKind.Table && s.DisplayText == "__MigrationHistory");
        Assert.Equal("__MigrationHistory", hit.FilterText);   // typing __Mig must keep matching
        Assert.DoesNotContain("\"", hit.DisplayText);
    }

    [Fact]
    public void A_mixed_case_column_is_inserted_quoted()
        => Assert.Equal("\"MigrationId\"",
            Replacement("select * from \"__MigrationHistory\" mh where mh.", "MigrationId", SuggestionKind.Column));

    [Fact]
    public void An_ordinary_column_of_a_quoted_relation_stays_bare()
        => Assert.Equal("user_id",
            Replacement("select * from \"__MigrationHistory\" mh where mh.", "user_id", SuggestionKind.Column));

    [Fact]
    public void A_synthesized_join_quotes_the_relation_it_joins()
        => Assert.Equal("\"__MigrationHistory\" mh on mh.user_id = u.id",
            Replacement("select * from users u join ", "__MigrationHistory", SuggestionKind.Join));

    [Fact]
    public void A_generated_predicate_qualifies_with_the_quotes_the_query_used()
    {
        // No alias on the mixed-case source: the predicate has to spell the qualifier the way the FROM
        // clause does, or the reference folds to __migrationhistory and the statement fails.
        var sql = "select * from \"__MigrationHistory\" join users u on u.";
        var fk = At(sql).Suggestions.FirstOrDefault(s => s.Kind == SuggestionKind.Join);
        Assert.NotNull(fk);
        Assert.Equal("id = \"__MigrationHistory\".user_id", fk!.ReplacementText);
    }

    [Fact]
    public void A_partially_typed_quoted_name_is_the_replacement_span()
    {
        // Accepting an item used to append: "__Mig"__MigrationHistory".
        var result = Engine.Complete("select * from \"__Mig", caretOffset: 20, Schema);
        Assert.Equal(14, result.ReplacementStart);
        Assert.Equal(6, result.ReplacementLength);
    }

    [Fact]
    public void A_quoted_qualifier_offers_that_relations_columns()
    {
        var sql = "select * from \"__MigrationHistory\" where \"__MigrationHistory\".";
        var cols = At(sql).Suggestions.Where(s => s.Kind == SuggestionKind.Column)
            .Select(s => s.DisplayText).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "MigrationId", "ProductVersion", "user_id" }, cols);
    }
}
