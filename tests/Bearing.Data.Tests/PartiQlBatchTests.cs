using Bearing.Data.DynamoDb;
using Bearing.Sql;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// How a PartiQL script is cut into statements and what each says about itself. Pure. The cut must never be
/// coarser than <c>Bearing.Sql</c>'s (the write guard's) reading of the same text, so the last test holds the
/// two side by side.
/// </summary>
public class PartiQlBatchTests
{
    [Fact]
    public void A_script_splits_on_semicolons_and_trims_each_statement()
    {
        var statements = PartiQlBatch.Split("  select * from \"A\" ;\n\tdelete from \"B\" where k = 1;insert into \"C\" value {'k': 1}  ");

        Assert.Equal(
            ["select * from \"A\"", "delete from \"B\" where k = 1", "insert into \"C\" value {'k': 1}"],
            statements);
    }

    [Theory]
    [InlineData("select * from \"T\" where a = 'x;y'", 1)]
    [InlineData("select * from \"T\" where a = 'it''s;here'", 1)]
    [InlineData("select * from \"a;b\"", 1)]
    [InlineData("select * from \"a\"\"b;c\"", 1)]
    [InlineData("select * from \"T\" -- one; two\n where a = 1", 1)]
    [InlineData("select * from \"T\" /* one; two */ where a = 1", 1)]
    [InlineData("select 1; select 2", 2)]
    [InlineData("select 'a'; select \"b\"; select 3 -- c", 3)]
    public void A_semicolon_splits_only_outside_strings_quoted_names_and_comments(string script, int count)
        => Assert.Equal(count, PartiQlBatch.Split(script).Count);

    [Fact]
    public void Comments_inside_a_statement_are_kept_as_typed()
    {
        var statements = PartiQlBatch.Split("-- header\nselect * /* all */ from \"T\"; -- tail\n");

        Assert.Equal(["-- header\nselect * /* all */ from \"T\""], statements);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData(";;  ;")]
    [InlineData("-- only a comment")]
    [InlineData("/* only */ ; -- comments\n ;")]
    public void Empty_statements_are_dropped(string script)
        => Assert.Empty(PartiQlBatch.Split(script));

    [Fact]
    public void Empty_statements_between_real_ones_are_dropped()
        => Assert.Equal(["select 1", "select 2"], PartiQlBatch.Split(";; select 1 ;  ; -- gap\n; select 2;"));

    [Fact]
    public void A_block_comment_does_not_nest()
        // After "*/" the text is code again, so the semicolon splits; a nested reading would swallow it.
        => Assert.Equal(["select 1 /* a /* b */", "select 2 */"], PartiQlBatch.Split("select 1 /* a /* b */; select 2 */"));

    [Theory]
    [InlineData("select 'open; select 2")]
    [InlineData("select \"open; select 2")]
    [InlineData("select 1 /* open; select 2")]
    public void An_unterminated_string_name_or_comment_runs_to_the_end(string script)
        => Assert.Single(PartiQlBatch.Split(script));

    [Theory]
    [InlineData("select * from \"T\"", "SELECT")]
    [InlineData("  SeLeCt 1", "SELECT")]
    [InlineData("insert into \"T\" value {'k': 1}", "INSERT")]
    [InlineData("update \"T\" set a = 1 where k = 1", "UPDATE")]
    [InlineData("delete from \"T\" where k = 1", "DELETE")]
    [InlineData("-- note\n/* more */ delete from \"T\" where k = 1", "DELETE")]
    [InlineData("exec foo", "EXEC")]
    [InlineData("\"select\" from x", "")]
    [InlineData("(select 1)", "")]
    [InlineData("", "")]
    public void The_lead_verb_is_the_first_keyword_upper_cased(string statement, string verb)
        => Assert.Equal(verb, PartiQlBatch.LeadVerb(statement));

    [Theory]
    [InlineData("select * from \"Orders\"", "Orders", null)]
    [InlineData("select * from Orders", "Orders", null)]
    [InlineData("SELECT a, b FROM \"Orders\" WHERE a = 1", "Orders", null)]
    [InlineData("select * from \"Orders\".\"ByCustomer\" where c = 'x'", "Orders", "ByCustomer")]
    [InlineData("select * from Orders.ByCustomer", "Orders", "ByCustomer")]
    [InlineData("select * from \"a\"\"b\"", "a\"b", null)]
    [InlineData("select * from \"my-table.v2\"", "my-table.v2", null)]
    [InlineData("-- c\nselect * /* from nowhere */ from \"T\"", "T", null)]
    [InlineData("insert into \"Orders\" value {'k': 1}", "Orders", null)]
    [InlineData("INSERT INTO Orders VALUE {'k': 1}", "Orders", null)]
    [InlineData("update \"Orders\" set a = 1 where k = 1", "Orders", null)]
    [InlineData("update Orders set a = 1", "Orders", null)]
    [InlineData("delete from \"Orders\" where k = 1", "Orders", null)]
    [InlineData("delete from Orders", "Orders", null)]
    public void The_target_is_the_table_and_index_the_statement_names(string statement, string table, string? index)
    {
        var target = PartiQlBatch.Target(statement);

        Assert.NotNull(target);
        Assert.Equal(table, target.Table);
        Assert.Equal(index, target.Index);
    }

    [Theory]
    [InlineData("select 1")]
    [InlineData("select * from")]
    [InlineData("select * from 'Orders'")]
    [InlineData("insert \"Orders\" value {}")]
    [InlineData("update")]
    [InlineData("delete \"Orders\"")]
    [InlineData("exec foo from \"T\"")]
    [InlineData("")]
    public void A_statement_without_a_readable_target_has_none(string statement)
        => Assert.Null(PartiQlBatch.Target(statement));

    [Theory]
    [InlineData("select * from \"T\"; delete from \"T\" where k = 1")]
    [InlineData("select 'a;b' from \"T\"; delete from \"T\"")]
    [InlineData("select * from \"T\" -- x; y\n; delete from \"T\"")]
    [InlineData("select * from \"T\" /* x; y */; delete from \"T\"")]
    [InlineData("select 1 /* a /* b */; delete from \"T\" /* c */")]
    [InlineData("select 'open; delete from \"T\"")]
    public void The_split_is_never_coarser_than_the_write_guards(string script)
    {
        var guard = PartiQlDialect.Instance.DescribeStatements(script).Count;

        Assert.True(PartiQlBatch.Split(script).Count >= guard);
    }

    /// <summary>
    /// The executor's split and the guard's split side by side over a corpus of PartiQL's own punctuation (ARCH4):
    /// wherever the guard reads the batch as PartiQL, the two find the same statements with the same lead verbs, in
    /// order — so what the guard judged is what is sent. Where it cannot (a Postgres-only form), every statement is
    /// marked unreadable and the executor's split is still never coarser.
    /// </summary>
    [Theory]
    [InlineData("select * from \"T\" where pk = 1")]
    [InlineData("select * from \"T\" where pk = 1; delete from \"T\" where pk = 1")]
    [InlineData("insert into \"T\" value {'pk': 1, 'a': [1, 2], 'b': <<'x', 'y'>>}; select * from \"T\"")]
    [InlineData("update \"T\" set a = {'k': 'v;w'} where pk = 'p;q'; select a from \"T\" where pk = ?")]
    [InlineData("SELECT * FROM \"Orders\".\"byStatus\" WHERE status IN ['open', 'held'];\nexists(select * from \"T\")")]
    [InlineData("-- lead\nselect * from \"a;b\" /* c; d */ where \"x\"\"y\" = 'it''s';  ;  select 2")]
    [InlineData("begin transaction; commit")]
    [InlineData("select * from \"T\" where pk = $$x; y$$; delete from \"T\"")]
    [InlineData("/* /* */ select * from \"Big\"; delete from \"T\" /* */")]
    public void The_split_and_the_guards_split_agree_statement_for_statement(string script)
    {
        var dialect = PartiQlDialect.Instance;
        var split = PartiQlBatch.Split(script);
        var spans = dialect.SplitStatements(script).Where(s => !string.IsNullOrWhiteSpace(Code(s.Text))).ToList();
        // A bare `;` between statements is a statement to the guard (risky, by the conservative default) and
        // nothing to the executor, which drops it unsent — so it is left out of the comparison, not argued away.
        var guard = dialect.DescribeStatements(script).Where(s => s.Verb != ";").ToList();

        Assert.True(split.Count >= spans.Count);
        Assert.True(split.Count >= guard.Count);

        if (guard.All(s => s.GuardIsDialectAware))
        {
            Assert.Equal(spans.Count, split.Count);
            Assert.Equal(guard.Count, split.Count);
            Assert.Equal(guard.Select(s => s.Verb), split.Select(PartiQlBatch.LeadVerb));
        }
        else
        {
            Assert.All(guard, s => Assert.False(s.GuardIsDialectAware));
        }

        // What is left of a span once its comments and separators are taken away — empty for a span that holds
        // only those, which the executor never sends.
        static string Code(string text)
            => string.Concat(PartiQlBatch.Split(text)).Trim();
    }
}
