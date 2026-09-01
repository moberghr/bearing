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

    // ---- the keyword the insertion has to supply (#75) ----------------------------------------

    [Fact]
    public void A_join_accepted_after_a_bare_source_brings_its_own_keyword()
    {
        // The reported case: `from users u ` with no `join` typed. The insertion used to be
        // `orders o on o.user_id = u.id`, producing `from users u orders o on …` — invalid SQL.
        var orders = JoinsFor("select * from users u ").SingleOrDefault(j => j.DisplayText == "orders");
        Assert.NotNull(orders);
        Assert.Equal("join orders o on o.user_id = u.id", orders!.ReplacementText);
    }

    [Fact]
    public void No_join_is_offered_while_the_caret_is_still_the_sources_alias_slot()
    {
        // `from users |` is where the alias goes, and that rule predates this one: a relation offered there
        // would overwrite the alias the user is about to type. Pinned so the keyword fix above is not read
        // as "offer a join wherever a source precedes the caret".
        Assert.Empty(JoinsFor("select * from users "));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("left outer")]
    [InlineData("right")]
    [InlineData("inner")]
    [InlineData("full")]
    public void A_typed_join_qualifier_is_completed_with_the_missing_keyword(string qualifier)
    {
        var orders = JoinsFor($"select * from users u {qualifier} ").SingleOrDefault(j => j.DisplayText == "orders");
        Assert.NotNull(orders);
        Assert.Equal("join orders o on o.user_id = u.id", orders!.ReplacementText);
    }

    [Theory]
    [InlineData("cross")]
    [InlineData("natural")]
    public void No_fk_join_is_offered_after_a_qualifier_that_takes_no_on_clause(string qualifier)
        // `cross join t x on …` and `natural join t x on …` are syntax errors, not a missing keyword — an
        // FK-equality suggestion has no valid shape here, so there is nothing correct to offer.
        => Assert.Empty(JoinsFor($"select * from users u {qualifier} "));

    [Fact]
    public void A_typed_join_keyword_is_not_repeated()
    {
        var orders = JoinsFor("select * from users u left join ").SingleOrDefault(j => j.DisplayText == "orders");
        Assert.NotNull(orders);
        Assert.Equal("orders o on o.user_id = u.id", orders!.ReplacementText);
    }

    [Fact]
    public void No_join_is_offered_after_a_comma()
    {
        // A comma-separated source cannot carry an `on` clause — the predicate belongs in the WHERE — so
        // there is no correct insertion to offer here.
        Assert.Empty(JoinsFor("select * from users u, "));
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
    public void The_join_hint_names_the_source_relation_not_just_its_alias()
    {
        // "join → u" made you guess which source a single-letter alias was; "join → users u" doesn't.
        var joins = JoinsFor("select * from users u join ");
        var orders = joins.Single(j => j.DisplayText == "orders");
        Assert.Equal("join → users u", orders.DetailText);

        // An unaliased source has only its name to show.
        Assert.Equal("join → users", JoinsFor("select * from users join ").First().DetailText);
    }

    [Fact]
    public void The_fk_predicate_hint_names_the_other_source_too()
    {
        var sql = "select * from users u join orders o on o.";
        var fk = Engine.Complete(sql, sql.Length, Schema).Suggestions
            .First(s => s.Kind == SuggestionKind.Join);
        Assert.Equal("fk → users u", fk.DetailText);
    }

    [Fact]
    public void Fk_predicate_is_offered_where_a_predicate_starts()
    {
        // Directly after ON: the whole equality is the useful completion.
        var sql = "select * from users u join orders o on o.";
        var result = Engine.Complete(sql, sql.Length, Schema);
        Assert.Contains(result.Suggestions,
            s => s.Kind == SuggestionKind.Join && s.ReplacementText == "user_id = u.id");
    }

    [Theory]
    // Right-hand side of an existing comparison — the reported case. Offering the equality again yielded
    // `on o.user_id = u.id = o.user_id`.
    [InlineData("select * from users u join orders o on o.user_id = u.")]
    [InlineData("select * from users u join orders o on o.user_id = u.id")]
    [InlineData("select * from users u join orders o on o.user_id > u.")]
    public void No_fk_predicate_where_only_a_column_can_go(string sql)
    {
        var result = Engine.Complete(sql, sql.Length, Schema);
        Assert.DoesNotContain(result.Suggestions, s => s.Kind == SuggestionKind.Join);
        Assert.Contains(result.Suggestions, s => s.Kind == SuggestionKind.Column);
    }

    [Fact]
    public void No_fk_predicate_in_a_select_list()
    {
        // A select list is a column position, not a predicate one (caret sits at "select u.|").
        var result = Engine.Complete("select u. from users u", caretOffset: 9, Schema);
        Assert.DoesNotContain(result.Suggestions, s => s.Kind == SuggestionKind.Join);
        Assert.Contains(result.Suggestions, s => s.Kind == SuggestionKind.Column);
    }

    [Fact]
    public void Fk_predicate_survives_a_conjunction()
    {
        var sql = "select * from users u join orders o on o.user_id = u.id and o.";
        var result = Engine.Complete(sql, sql.Length, Schema);
        Assert.Contains(result.Suggestions, s => s.Kind == SuggestionKind.Join);
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
