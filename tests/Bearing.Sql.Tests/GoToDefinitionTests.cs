using Bearing.Core.Schema;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// F12 on the identifier under the caret (#117). The cases that matter are the two ends: an alias resolving
/// to its table, and every shape it must <em>refuse</em> — a jump to the wrong table is silent, and the user
/// then reads a schema that is not the one their query is about.
/// </summary>
public class GoToDefinitionTests
{
    private static readonly ISchemaSnapshot Schema = TestSchema.Build();

    /// <summary>Resolve with the caret at the <c>|</c> in the given SQL.</summary>
    private static DefinitionTarget? At(string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "the fixture must mark the caret with |");
        return GoToDefinition.Resolve(sqlWithCaret.Remove(caret, 1), caret, Schema);
    }

    private static void Assert_Table(DefinitionTarget? target, string schema, string name)
    {
        Assert.NotNull(target);
        Assert.Equal(schema, target.Schema);
        Assert.Equal(name, target.Name);
        Assert.Null(target.Column);
    }

    private static void Assert_Column(DefinitionTarget? target, string schema, string name, string column)
    {
        Assert.NotNull(target);
        Assert.Equal(schema, target.Schema);
        Assert.Equal(name, target.Name);
        Assert.Equal(column, target.Column);
    }

    // ---- relations ------------------------------------------------------------------------------------

    [Fact]
    public void The_caret_on_a_table_name_resolves_it()
        => Assert_Table(At("select * from ord|ers"), "public", "orders");

    [Theory]
    [InlineData("select * from |orders")]     // at the first character
    [InlineData("select * from ord|ers")]     // inside
    [InlineData("select * from orders|")]     // immediately after, where a double-click leaves it
    public void Anywhere_on_the_word_counts_as_on_it(string sql)
        => Assert_Table(At(sql), "public", "orders");

    [Fact]
    public void A_schema_qualified_name_resolves_outside_the_search_path()
    {
        // audit is in the catalog but not on the search_path, so the qualified form is the only one that
        // resolves — and it is exactly the case someone reaches for F12 on.
        Assert_Table(At("select * from audit.ev|ents"), "audit", "events");
        Assert_Table(At("select * from aud|it.events"), "audit", "events");
    }

    [Fact]
    public void A_bare_name_outside_the_search_path_still_resolves_when_only_one_relation_has_it()
    {
        // Postgres would not resolve this without qualification, and the tool does — deliberately, because
        // it is the snapshot's own rule (`ISchemaSnapshot.ResolveTable` with a null schema, the same one
        // completion resolves against), and because there is exactly one relation with the name. F12 is
        // answering "what is this word", not "would this query run".
        Assert_Table(At("select * from ev|ents"), "audit", "events");
    }

    [Fact]
    public void A_relation_named_like_a_keyword_still_resolves()
        => Assert_Table(At("select * from ord|er"), "public", "order");

    [Fact]
    public void A_quoted_mixed_case_relation_resolves()
        => Assert_Table(At("""select * from "__Migratio|nHistory" """), "public", "__MigrationHistory");

    // ---- aliases --------------------------------------------------------------------------------------

    [Fact]
    public void An_alias_resolves_to_its_table()
    {
        // The headline case: the alias is not a name in the catalog, so nothing but the query's own FROM
        // clause can say what it means.
        Assert_Table(At("select o.total from orders |o"), "public", "orders");
        Assert_Table(At("select * from orders as o join users |u on u.id = o.user_id"), "public", "users");
    }

    [Fact]
    public void The_caret_on_a_qualifier_means_the_table_and_on_the_column_means_the_column()
    {
        // The same reference read two ways. Someone with the caret on `o` is asking what `o` is; someone on
        // `total` is asking about the column. Resolving the whole chain regardless would answer only the
        // second question.
        Assert_Table(At("select |o.total from orders o"), "public", "orders");
        Assert_Column(At("select o.|total from orders o"), "public", "orders", "total");
    }

    [Fact]
    public void The_caret_on_the_schema_of_a_three_part_reference_means_the_relation()
    {
        Assert_Table(At("select aud|it.events.payload from audit.events"), "audit", "events");
        Assert_Table(At("select audit.ev|ents.payload from audit.events"), "audit", "events");
    }

    [Fact]
    public void An_alias_that_shadows_a_real_relation_still_means_the_alias()
    {
        // `users` aliased as `order` — a relation of that name exists, and the query's own declaration wins.
        Assert_Table(At("select * from users order_alias, orders |order_alias"), "public", "users");
    }

    [Fact]
    public void An_alias_declared_in_another_statement_is_not_in_scope()
    {
        // A buffer is a script, and F12 is scoped to the statement under the caret — the same rule execution
        // follows.
        Assert.Null(At("select * from orders o;\nselect * from users where |o = 1"));
    }

    // ---- columns --------------------------------------------------------------------------------------

    [Fact]
    public void A_qualified_column_resolves_to_that_column()
        => Assert_Column(At("select o.to|tal from orders o"), "public", "orders", "total");

    [Fact]
    public void A_column_qualified_by_the_table_s_own_name_resolves()
        => Assert_Column(At("select orders.to|tal from orders"), "public", "orders", "total");

    [Fact]
    public void A_three_part_reference_resolves_to_the_column()
        => Assert_Column(At("select audit.events.pay|load from audit.events"), "audit", "events", "payload");

    [Fact]
    public void A_qualifier_whose_column_the_snapshot_lacks_still_lands_on_the_table()
    {
        // A computed alias, or a snapshot older than the column. The table is the closest true answer, and
        // saying nothing would be less useful than landing one level up.
        Assert_Table(At("select o.no_such_col|umn from orders o"), "public", "orders");
    }

    [Fact]
    public void An_unqualified_column_resolves_when_exactly_one_source_has_it()
        => Assert_Column(At("select to|tal from orders o"), "public", "orders", "total");

    [Fact]
    public void An_unqualified_column_that_two_sources_share_resolves_to_neither()
    {
        // `id` on both sides of a join is the commonest shape in any schema. Picking one would be a coin
        // toss presented as an answer.
        Assert.Null(At("select 1 from orders o join users u on u.id = o.user_id where i|d = 1"));
    }

    [Fact]
    public void A_column_name_that_is_also_a_keyword_resolves()
    {
        // `name` is a keyword to the grammar and an ordinary column to everyone else. Excluding keywords
        // from the identifier test would make F12 fail on exactly these.
        Assert_Column(At("select na|me from users"), "public", "users", "name");
    }

    [Fact]
    public void A_quoted_mixed_case_column_resolves()
        => Assert_Column(
            At("""select h."Migratio|nId" from "__MigrationHistory" h"""),
            "public", "__MigrationHistory", "MigrationId");

    // ---- what it refuses ------------------------------------------------------------------------------

    [Theory]
    [InlineData("sel|ect * from orders")]                  // a keyword that is not a name in the catalog
    [InlineData("select * from orders where total > 1|0")] // a number
    [InlineData("select 'ord|ers' from orders")]           // inside a string literal
    [InlineData("select * from |")]                        // nothing there yet
    [InlineData("|")]
    [InlineData("select * from nosuchtab|le")]             // not in the catalog
    public void Anything_it_cannot_place_resolves_to_nothing(string sql)
        => Assert.Null(At(sql));

    [Fact]
    public void A_dot_inside_a_string_is_not_read_as_a_qualifier()
    {
        // Only the lexer knows this dot is text — the same reason §1.3 gives for the redactor being
        // lexer-based rather than a regex.
        Assert.Null(At("select 'o.to|tal' from orders o"));
    }

    [Fact]
    public void The_name_being_typed_is_not_yet_a_destination()
    {
        // At `from orde|` the word is a relation being chosen, not one in scope — and a partial name that
        // happened to match some other table would jump somewhere the query does not refer to.
        Assert.Null(At("select * from orde|"));
    }

    [Fact]
    public void A_cte_name_is_not_a_catalog_object()
    {
        // It resolves to nothing rather than to a table that happens to share its name.
        Assert.Null(At("with recent as (select * from orders) select * from rec|ent"));
    }

    [Fact]
    public void An_out_of_range_caret_is_clamped_rather_than_throwing()
    {
        Assert.Null(GoToDefinition.Resolve("select * from orders", -5, Schema));
        Assert.NotNull(GoToDefinition.Resolve("select * from orders", 999, Schema));
    }
}
