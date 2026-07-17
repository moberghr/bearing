using Squirrel.Core.Completion;
using Xunit;

namespace Squirrel.Sql.Tests;

public class JoinCompletionTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Squirrel.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

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
