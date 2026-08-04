using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

public class JoinCompletionTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    private static Suggestion[] JoinsFor(string sql)
        => Engine.Complete(sql, sql.Length, Schema).Suggestions.Where(s => s.Kind == SuggestionKind.Join).ToArray();

    [Fact]
    public void Join_after_a_source_synthesizes_fk_join_from_referenced_side()
    {
        // users is the referenced side of orders.user_id -> users.id
        var joins = JoinsFor("select * from users u join ");
        var orders = joins.SingleOrDefault(j => j.DisplayText == "orders");
        Assert.NotNull(orders);
        Assert.Equal("orders o on o.user_id = u.id", orders!.ReplacementText);
    }

    [Fact]
    public void Join_after_a_source_synthesizes_fk_join_from_referencing_side()
    {
        // orders is the referencing side; joining back to users
        var joins = JoinsFor("select * from orders o join ");
        var users = joins.SingleOrDefault(j => j.DisplayText == "users");
        Assert.NotNull(users);
        Assert.Equal("users u on u.id = o.user_id", users!.ReplacementText);
    }

    [Fact]
    public void No_join_suggestions_before_any_source_is_present()
    {
        var joins = JoinsFor("select * from ");
        Assert.Empty(joins);
    }

    [Fact]
    public void Alias_dot_inside_lateral_subquery_offers_inner_columns_and_fk_predicate()
    {
        // Mirrors: outer `from users u`, inner (still-open) `join lateral (… from orders o where o.`
        var sql = "select * from users u\n join lateral (\n select count(*)\n from orders o\n where o.";
        var result = Engine.Complete(sql, sql.Length, Schema);

        // The inner alias resolves to orders' columns (the reported "no columns for o." case).
        var cols = result.Suggestions.Where(s => s.Kind == SuggestionKind.Column).Select(s => s.DisplayText).ToList();
        Assert.Contains("id", cols);
        Assert.Contains("user_id", cols);

        // Instead of a bogus join clause, a ready-made FK equality against the in-scope outer alias.
        Assert.Contains(result.Suggestions,
            s => s.Kind == SuggestionKind.Join && s.ReplacementText == "user_id = u.id");

        // No table/keyword noise after "alias.".
        Assert.DoesNotContain(result.Suggestions, s => s.Kind == SuggestionKind.Table);
        Assert.DoesNotContain(result.Suggestions, s => s.Kind == SuggestionKind.Keyword);
    }

    [Fact]
    public void Alias_dot_restricts_columns_to_that_source()
    {
        var result = Engine.Complete("select u. from users u", caretOffset: 9, Schema);
        var cols = result.Suggestions.Where(s => s.Kind == SuggestionKind.Column).Select(s => s.DisplayText).ToList();
        Assert.Equal(new[] { "email", "id", "name" }, cols.OrderBy(x => x).ToArray());
        // bare replacement because the alias + dot are already typed
        Assert.All(result.Suggestions.Where(s => s.Kind == SuggestionKind.Column),
            s => Assert.DoesNotContain(".", s.ReplacementText));
    }
}
