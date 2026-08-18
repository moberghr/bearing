using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Where an alias belongs, another relation does not. c3 classifies an alias slot as a table position
/// (an alias is just an identifier), so completion used to offer every relation there — and accepting one
/// overwrote the alias instead of the name: <c>select * from film f</c> became
/// <c>select * from film film f2</c>.
/// </summary>
public class AliasSlotCompletionTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    private static Suggestion[] At(string sql) => Engine.Complete(sql, sql.Length, Schema).Suggestions.ToArray();

    private static void AssertNoRelations(string sql)
    {
        var offered = At(sql);
        Assert.DoesNotContain(offered, s => s.Kind is SuggestionKind.Table or SuggestionKind.View
            or SuggestionKind.Join or SuggestionKind.Schema);
    }

    [Theory]
    [InlineData("select * from users u")]              // typing the alias
    [InlineData("select * from users ")]               // the alias slot, still empty
    [InlineData("select * from users as u")]
    [InlineData("select * from audit.events e")]       // schema-qualified source
    [InlineData("select * from users u join orders o")]
    [InlineData("select * from \"__MigrationHistory\" m")]
    public void No_relation_is_offered_in_an_alias_slot(string sql) => AssertNoRelations(sql);

    [Theory]
    [InlineData("select * from users u")]
    [InlineData("select * from users as u")]
    [InlineData("select * from audit.events e")]
    public void Nothing_at_all_is_offered_while_an_alias_is_being_typed(string sql)
        => Assert.Empty(At(sql));

    [Fact]
    public void Keywords_still_come_through_in_an_empty_alias_slot()
    {
        // What actually follows a named source: as / join / where. Suppressing relations must not leave
        // the popup empty-handed.
        var keywords = At("select * from users ").Where(s => s.Kind == SuggestionKind.Keyword)
            .Select(s => s.DisplayText).ToList();
        Assert.NotEmpty(keywords);
    }

    [Theory]
    [InlineData("select * from ")]                     // nothing named yet
    [InlineData("select * from us")]                   // partially-typed relation name
    [InlineData("select * from users u, ")]            // next item in the FROM list
    [InlineData("select * from users u join ")]        // the join's own relation
    [InlineData("select * from nosuchtable f")]        // unresolved name: not an alias slot we can trust
    public void Relations_are_still_offered_where_a_relation_belongs(string sql)
    {
        Assert.Contains(At(sql), s => s.Kind is SuggestionKind.Table);
    }

    [Fact]
    public void The_replacement_span_over_an_alias_stays_the_alias()
    {
        // Belt and braces on the reported corruption: if anything were offered here, it would overwrite
        // the alias token — so the span itself is worth pinning.
        var result = Engine.Complete("select * from users u", caretOffset: 21, Schema);
        Assert.Equal(20, result.ReplacementStart);
        Assert.Equal(1, result.ReplacementLength);
    }
}
