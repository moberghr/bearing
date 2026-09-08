using System.Linq;
using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Completion inside the write statements. It offered nothing useful in any of them: no table after
/// <c>update</c>, <c>insert into</c> or <c>delete from</c>, no column in a <c>SET</c>, and an UPDATE's
/// <c>WHERE</c> completed against every table in the database rather than the one being written.
/// <para>
/// Three separate causes, all of them the same shape — the rules that describe a write statement are not
/// the rules that describe a SELECT:
/// <list type="number">
/// <item><c>table_ref</c> is the FROM-clause rule. An UPDATE/DELETE target is
/// <c>relation_expr_opt_alias</c> and an INSERT's is <c>insert_target</c>, so no write statement ever
/// reported a table position.</item>
/// <item>A SET target is <c>set_target</c> and an insert column is <c>insert_column_item</c>; neither is
/// <c>columnref</c>, so neither reported a column position.</item>
/// <item><c>FromClauseExtractor</c> only knew FROM and JOIN, so an UPDATE had no sources at all and its
/// columns could not be scoped. (DELETE worked by accident: its target follows a FROM.)</item>
/// </list>
/// </para>
/// </summary>
public class WriteStatementCompletionTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    private static (string[] Tables, string[] Columns) At(string sql)
    {
        var result = Engine.Complete(sql, sql.Length, Schema);
        return (
            result.Suggestions.Where(s => s.Kind == SuggestionKind.Table).Select(s => s.DisplayText).ToArray(),
            result.Suggestions.Where(s => s.Kind == SuggestionKind.Column).Select(s => s.DisplayText).ToArray());
    }

    // ---- the table each write statement names -------------------------------------------------------

    [Theory]
    [InlineData("update us")]
    [InlineData("insert into us")]
    [InlineData("delete from us")]
    [InlineData("update ")]
    [InlineData("insert into ")]
    // The reported shape: a completed statement above, then the one being typed.
    [InlineData("select * from users u;\n\nupdate us")]
    public void A_write_statements_target_is_a_table_position(string sql)
    {
        var (tables, _) = At(sql);
        Assert.Contains("users", tables);
        Assert.Contains(CompletionIntent.TablePosition, Engine.IntentsAt(sql, sql.Length));
    }

    // ---- the columns each write statement can name --------------------------------------------------

    [Theory]
    [InlineData("update users set ")]
    [InlineData("update users set na")]
    [InlineData("update users set name = 'x', ")]
    [InlineData("insert into users (")]
    [InlineData("insert into users (i")]
    [InlineData("insert into users (id, ")]
    public void A_write_statements_column_slots_offer_that_tables_columns(string sql)
    {
        var (_, columns) = At(sql);
        Assert.Equal(new[] { "id", "email", "name" }.OrderBy(c => c), columns.OrderBy(c => c));
        Assert.Contains(CompletionIntent.ColumnPosition, Engine.IntentsAt(sql, sql.Length));
    }

    /// <summary>
    /// The scoping half. An UPDATE has no FROM, so before this its WHERE fell back to every column of every
    /// table — thirteen of them here, including four different <c>id</c>s.
    /// </summary>
    [Theory]
    [InlineData("update users set name = 'x' where ")]
    [InlineData("delete from users where ")]
    public void A_write_statements_predicate_is_scoped_to_its_target(string sql)
    {
        var (_, columns) = At(sql);
        Assert.Equal(new[] { "id", "email", "name" }.OrderBy(c => c), columns.OrderBy(c => c));
    }

    [Fact]
    public void An_update_with_a_from_clause_sees_both_sides()
    {
        var (_, columns) = At("update users set name = orders.id from orders where ");
        Assert.Contains("email", columns);        // the target
        Assert.Contains("total", columns);        // the FROM source
    }

    // ---- the keywords that name nothing --------------------------------------------------------------

    /// <summary>
    /// UPDATE and INTO also appear where no table follows. Treating those as sources would put a phantom
    /// relation in scope and complete against it.
    /// </summary>
    [Fact]
    public void For_update_does_not_invent_a_source()
    {
        // `users` is the only real source here; the trailing FOR UPDATE must not add another.
        var (_, columns) = At("select * from users where ");
        var (_, afterForUpdate) = At("select * from users for update");
        Assert.Equal(new[] { "id", "email", "name" }.OrderBy(c => c), columns.OrderBy(c => c));
        Assert.Empty(afterForUpdate);
    }

    [Fact]
    public void On_conflict_do_update_set_completes_the_target_not_a_table_called_set()
    {
        var (_, columns) = At("insert into users (id) values (1) on conflict (id) do update set ");
        Assert.Equal(new[] { "id", "email", "name" }.OrderBy(c => c), columns.OrderBy(c => c));
    }

    /// <summary>A <c>SELECT … INTO t</c> creates <c>t</c>; it does not read it, so it is not a source.</summary>
    [Fact]
    public void Select_into_does_not_treat_the_new_table_as_a_source()
    {
        var (_, columns) = At("select * into newtbl from users where ");
        Assert.Equal(new[] { "id", "email", "name" }.OrderBy(c => c), columns.OrderBy(c => c));
    }

    // ---- what must NOT be offered --------------------------------------------------------------------

    /// <summary>
    /// The slot after a write statement's target is its alias, and nothing else may go there. Reporting a
    /// table position made it offer every relation *and* a join snippet, so accepting the first suggestion
    /// gave <c>UPDATE users JOIN orders o ON …</c> — a syntax error, from one keypress.
    /// </summary>
    [Theory]
    [InlineData("update users ")]
    [InlineData("insert into users ")]
    [InlineData("delete from users ")]
    [InlineData("merge into users ")]
    public void The_alias_slot_of_a_write_target_offers_no_relations_and_no_joins(string sql)
    {
        var result = Engine.Complete(sql, sql.Length, Schema);
        Assert.DoesNotContain(result.Suggestions, s => s.Kind is SuggestionKind.Table or SuggestionKind.View);
        Assert.DoesNotContain(result.Suggestions, s => s.Kind == SuggestionKind.Join);
    }

    /// <summary>
    /// Postgres' grammar is <c>INSERT INTO t [ AS alias ]</c> — the <c>AS</c> is mandatory, unlike
    /// UPDATE/DELETE where it is optional. So the bare <c>users u</c> that suits a FROM clause is a syntax
    /// error here, and every accepted table completion in an INSERT produced one.
    /// </summary>
    [Fact]
    public void An_insert_target_is_inserted_without_a_bare_alias()
    {
        var inserted = Engine.Complete("insert into ", 12, Schema)
            .Suggestions.First(s => s.DisplayText == "users").ReplacementText;
        Assert.Equal("users", inserted);
    }

    [Fact]
    public void A_from_clause_still_gets_its_alias()
    {
        var inserted = Engine.Complete("select * from ", 14, Schema)
            .Suggestions.First(s => s.DisplayText == "users").ReplacementText;
        Assert.Equal("users u", inserted);
    }

    /// <summary>
    /// <c>UPDATE users u SET u.name = …</c> is rejected by Postgres outright: a SET target is a bare column
    /// name. The qualification the general column path adds for an aliased source is exactly wrong here.
    /// </summary>
    [Theory]
    [InlineData("update users u set ")]
    [InlineData("insert into users as u (")]
    public void A_write_targets_columns_are_inserted_bare_even_when_the_target_is_aliased(string sql)
    {
        var inserted = Engine.Complete(sql, sql.Length, Schema)
            .Suggestions.Where(s => s.Kind == SuggestionKind.Column)
            .Select(s => s.ReplacementText);
        Assert.All(inserted, text => Assert.DoesNotContain(".", text));
    }

    /// <summary>
    /// Only the target's columns are assignable, however many relations the statement also reads. These are
    /// exactly the shapes where a general column scope misfires.
    /// </summary>
    [Theory]
    [InlineData("update users set  from orders", 17)]
    [InlineData("insert into users () select * from orders", 19)]
    public void Only_the_targets_columns_are_assignable(string sql, int caret)
    {
        var columns = Engine.Complete(sql, caret, Schema)
            .Suggestions.Where(s => s.Kind == SuggestionKind.Column).Select(s => s.DisplayText).ToArray();

        Assert.Equal(new[] { "id", "email", "name" }.OrderBy(c => c), columns.OrderBy(c => c));
        Assert.DoesNotContain("total", columns);      // orders' column: readable, not assignable
    }

    /// <summary>ONLY is inheritance scoping, not part of the name. Skipped over, the extractor found no
    /// source at all and fell back to every column in the database — the very failure this set fixes.</summary>
    [Theory]
    [InlineData("update only users set ")]
    [InlineData("delete from only users where ")]
    public void Only_before_the_target_does_not_lose_the_source(string sql)
    {
        var (_, columns) = At(sql);
        Assert.Equal(new[] { "id", "email", "name" }.OrderBy(c => c), columns.OrderBy(c => c));
    }

    /// <summary>MERGE is a write statement this codebase already knows about — <c>WriteGuard</c> flags it
    /// (§1.2) — so its target belongs in scope like any other.</summary>
    [Fact]
    public void Merge_scopes_its_target_and_its_source()
    {
        const string sql = "merge into users u using orders o on o.id = u.id when matched then update set ";
        var (_, columns) = At(sql);
        Assert.Equal(new[] { "id", "email", "name" }.OrderBy(c => c), columns.OrderBy(c => c));
    }

    // ---- the caret, one character earlier than it looked ---------------------------------------------

    /// <summary>
    /// A caret sitting exactly at the end of a non-word token is <b>past</b> it, not on it. Reporting the
    /// token's own index asked c3 what may appear <i>where that token is</i> — a different question, and for
    /// <c>(</c> one whose answer includes a parenthesised join. So <c>insert into users (</c> offered tables
    /// where the column list belongs, and typing a single letter fixed it, which is the tell.
    /// <para>A word is the exception and must stay so: <c>us|</c> is still the word being completed.</para>
    /// </summary>
    [Fact]
    public void A_caret_just_past_a_punctuation_token_is_after_it()
    {
        var (tables, columns) = At("insert into users (");
        Assert.Empty(tables);
        Assert.NotEmpty(columns);
    }

    [Fact]
    public void A_caret_just_past_a_half_typed_word_is_still_inside_it()
    {
        // The replacement span is what proves it: the popup must overwrite "us", not insert beside it.
        var result = Engine.Complete("select * from us", 16, Schema);
        Assert.Equal(14, result.ReplacementStart);
        Assert.Equal(2, result.ReplacementLength);
        Assert.Contains("users", result.Suggestions.Select(s => s.DisplayText));
    }
}
