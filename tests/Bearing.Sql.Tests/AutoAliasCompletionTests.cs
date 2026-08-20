using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The auto-alias a table suggestion carries must not depend on the word the completion is about to
/// overwrite. That word is a half-typed relation name, but <see cref="FromClauseExtractor"/> reads it
/// as an in-scope source, so its own text used to count as a taken alias — completing at <c>from u</c>
/// offered <c>users u2</c> because "u" was "already taken" by the "u" being replaced.
/// </summary>
public class AutoAliasCompletionTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    /// <summary>The insertion text a table suggestion carries, with the caret at the end of the SQL.</summary>
    private static string AliasFor(string sql, string table)
        => Engine.Complete(sql, sql.Length, Schema).Suggestions
            .First(s => s.Kind is SuggestionKind.Table && s.DisplayText == table)
            .ReplacementText;

    [Fact]
    public void The_partial_name_being_completed_is_not_a_taken_alias()
        => Assert.Equal("users u", AliasFor("select * from u", "users"));

    [Theory]
    [InlineData("u")]
    [InlineData("us")]
    [InlineData("use")]
    [InlineData("user")]
    [InlineData("users")]
    public void The_alias_is_the_same_however_much_of_the_name_was_typed(string typed)
    {
        var sql = "select * from " + typed;
        Assert.Equal("users u", AliasFor(sql, "users"));
    }

    [Fact]
    public void An_alias_a_real_source_already_holds_is_still_taken()
    {
        // The `u` on users is a genuine in-scope alias, so a second users has to disambiguate.
        Assert.Equal("users u2", AliasFor("select * from users u join u", "users"));
    }

    [Fact]
    public void A_fully_named_unaliased_source_is_still_taken()
    {
        // `orders` is referred to by its own name here, so a relation whose base alias is "orders"
        // would collide — but "o" is free, and the "o" under the caret must not take it.
        Assert.Equal("orders o", AliasFor("select * from users join o", "orders"));
    }

    [Fact]
    public void A_join_suggestion_alias_also_ignores_the_word_under_the_caret()
    {
        const string sql = "select * from users u join o";
        var join = Engine.Complete(sql, sql.Length, Schema).Suggestions
            .First(s => s.Kind is SuggestionKind.Join && s.DisplayText == "orders");
        Assert.Equal("orders o on o.user_id = u.id", join.ReplacementText);
    }
}
