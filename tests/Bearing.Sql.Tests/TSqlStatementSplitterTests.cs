using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Splitting a T-SQL buffer — the read-side half of the dialect split. Every test here has a Postgres
/// counterpart that gets the buffer wrong, which is the whole point: the spans this produces are what
/// "run the statement at the caret" sends to the server, what the highlight margin draws, and what
/// completion scopes itself to.
/// <para>
/// The Postgres answers are asserted alongside deliberately. They are not a wish list of things to fix —
/// they are correct <em>for Postgres</em>, where <c>GO</c> is an identifier and <c>[a;b]</c> is not a name
/// — and they document why the dialect has to travel with the tab rather than being chosen once.
/// </para>
/// </summary>
public class TSqlStatementSplitterTests
{
    private static readonly ISqlDialect Ss = SqlServerDialect.Instance;
    private static readonly ISqlDialect Pg = PostgresDialect.Instance;

    private const string GoBatch = "select * from Orders\nGO\ndelete from Orders\nGO\n";

    [Fact]
    public void Go_on_its_own_line_ends_a_statement()
    {
        var spans = StatementSplitter.Split(Ss, GoBatch);

        Assert.Equal(2, spans.Count);
        Assert.Equal("select * from Orders", spans[0].Text);
        Assert.Equal("delete from Orders", spans[1].Text);
    }

    [Fact]
    public void The_go_itself_is_never_inside_a_span()
    {
        // GO is a client directive, not T-SQL: SqlClient cannot send it, so a span that carried one would
        // fail on the server the moment Run executed the statement under the caret.
        foreach (var span in StatementSplitter.Split(Ss, GoBatch))
            Assert.DoesNotContain("GO", span.Text);
    }

    [Fact]
    public void The_postgres_lexer_reads_the_same_batch_as_one_statement()
    {
        // Which is exactly the bug: "run current statement" ran the whole buffer on a SQL Server tab,
        // and the buffer it ran contained a token no server can parse.
        Assert.Single(StatementSplitter.Split(Pg, GoBatch));
        // The dialect-less overload is still the Postgres one, unchanged.
        Assert.Single(StatementSplitter.Split(GoBatch));
    }

    [Fact]
    public void A_semicolon_inside_a_delimited_name_is_not_a_boundary()
    {
        var spans = StatementSplitter.Split(Ss, "select * from [Order; Details]");

        var only = Assert.Single(spans);
        Assert.Equal("select * from [Order; Details]", only.Text);
        // The PG lexer has no delimited-identifier concept, so it splits the name in half.
        Assert.Equal(2, StatementSplitter.Split(Pg, "select * from [Order; Details]").Count);
    }

    [Fact]
    public void A_semicolon_inside_a_string_is_not_a_boundary()
    {
        var only = Assert.Single(StatementSplitter.Split(Ss, "select N'a;b' as x"));
        Assert.Equal("select N'a;b' as x", only.Text);
    }

    [Fact]
    public void Semicolons_still_split()
    {
        var spans = StatementSplitter.Split(Ss, "select 1; select 2");

        Assert.Equal(2, spans.Count);
        // The terminator is not part of the statement here, where a Postgres span would carry it (and the
        // whitespace after it). That is <see cref="TSqlScanner"/>'s existing shape — the same texts the
        // write-guard confirmation lists — and the separator has to stay outside the span anyway, because
        // for a GO it is a token no server will accept.
        Assert.Equal("select 1", spans[0].Text);
        Assert.Equal("select 2", spans[1].Text);
    }

    [Fact]
    public void Every_span_maps_back_onto_the_buffer_it_came_from()
    {
        // The offsets are load-bearing: the highlight margin paints TrimmedStart..TrimmedEnd, and folding
        // measures the first line from them. A span whose text does not sit at its own offset marks the
        // wrong region of the document.
        foreach (var span in StatementSplitter.Split(Ss, GoBatch))
        {
            Assert.Equal(span.Text, GoBatch.Substring(span.Start, span.Text.Length));
            Assert.Equal(span.Start, span.TrimmedStart);
            Assert.Equal(span.Start + span.Text.Length, span.TrimmedEnd);
        }
    }

    [Fact]
    public void A_leading_comment_stays_with_the_statement_it_documents()
    {
        var sql = "-- what this does\nselect 1\nGO\nselect 2";

        var spans = StatementSplitter.Split(Ss, sql);

        Assert.Equal(2, spans.Count);
        Assert.Equal("-- what this does\nselect 1", spans[0].Text);
        Assert.Equal(0, spans[0].Start);
    }

    [Theory]
    [InlineData(0)]                        // start of the first batch
    [InlineData(20)]                       // end of the first batch's text
    [InlineData(21)]                       // on the GO line: still the batch above it
    public void StatementAt_attributes_a_caret_before_the_separator_to_the_batch_above(int caret)
    {
        var stmt = StatementSplitter.StatementAt(Ss, GoBatch, caret);

        Assert.NotNull(stmt);
        Assert.Equal("select * from Orders", stmt!.Text);
    }

    [Fact]
    public void StatementAt_finds_the_batch_after_the_separator()
    {
        var caret = GoBatch.IndexOf("delete", System.StringComparison.Ordinal) + 3;

        var stmt = StatementSplitter.StatementAt(Ss, GoBatch, caret);

        Assert.Equal("delete from Orders", stmt!.Text);
    }

    [Fact]
    public void EnsureSeparated_turns_a_go_separated_batch_into_a_semicolon_separated_one()
    {
        // GO cannot be sent; ';' can. The statements that genuinely need a batch of their own still
        // cannot be run several-at-once, but every ordinary GO-separated run stops failing on the GO.
        var normalized = StatementSplitter.EnsureSeparated(Ss, GoBatch);

        Assert.Equal("select * from Orders;\ndelete from Orders;", normalized);
        Assert.DoesNotContain("GO", normalized);
    }

    [Fact]
    public void EnsureSeparated_leaves_a_single_t_sql_statement_alone()
        => Assert.Equal("select * from [Order; Details]",
            StatementSplitter.EnsureSeparated(Ss, "select * from [Order; Details]"));

    [Fact]
    public void EnsureSeparated_keeps_the_terminator_off_a_trailing_comment_line()
    {
        var normalized = StatementSplitter.EnsureSeparated(Ss, "select 1 -- note\nGO\nselect 2");

        Assert.Equal("select 1 -- note\n;\nselect 2;", normalized);
        // And the result re-splits into the two statements it claims to be.
        Assert.Equal(2, StatementSplitter.Split(Ss, normalized).Count);
    }

    [Fact]
    public void Folding_gives_a_region_per_go_separated_batch()
    {
        var sql = "select 1,\n       2\nfrom a\nGO\nselect 3,\n       4\nfrom b";

        Assert.Equal(2, SqlFolding.ComputeFoldRegions(Ss, sql).Count);
        // The PG lexer sees one statement here (no ';', no blank line), so it offers one region over the
        // whole buffer — folding either batch would have folded both.
        Assert.Single(SqlFolding.ComputeFoldRegions(Pg, sql));
    }

    // ---- BEGIN … END: a semicolon inside a body is not a statement boundary ---------------------

    /// <summary>
    /// The failure this closes. Run sends the statement under the caret, and with a paren-only notion of
    /// "top level" that was <c>create procedure p as begin select 1</c> — a fragment the server rejects,
    /// for a procedure the user was looking at whole.
    /// </summary>
    [Fact]
    public void A_procedure_body_is_one_statement()
    {
        const string sql = "create procedure p as begin select 1; select 2; end";

        var span = Assert.Single(StatementSplitter.Split(Ss, sql));
        Assert.Equal(sql, span.Text);
    }

    /// <summary>Nested blocks, and the statements either side of one: the body is whole and its
    /// neighbours are still separate.</summary>
    [Fact]
    public void Statements_around_a_block_still_split()
    {
        const string sql = "select 0; if @x = 1 begin if @y = 2 begin select 1; end select 2; end; select 3";

        var spans = StatementSplitter.Split(Ss, sql);

        Assert.Equal(3, spans.Count);
        Assert.Equal("select 0", spans[0].Text);
        Assert.Equal("if @x = 1 begin if @y = 2 begin select 1; end select 2; end", spans[1].Text);
        Assert.Equal("select 3", spans[2].Text);
    }

    /// <summary><c>BEGIN TRY</c> / <c>BEGIN CATCH</c> are blocks; their <c>END TRY</c> and
    /// <c>END CATCH</c> are one <c>END</c> token with a word after it, so the closer needs no case of its
    /// own.</summary>
    [Fact]
    public void A_try_catch_block_is_one_statement()
    {
        const string sql = "begin try select 1; select 2; end try begin catch select 3; end catch";

        Assert.Single(StatementSplitter.Split(Ss, sql));
    }

    /// <summary>
    /// The counter-case, and the reason <c>BEGIN</c> alone cannot be the opener: no <c>END</c> closes
    /// <c>BEGIN TRANSACTION</c>, so counting it would swallow the rest of the script into one statement —
    /// the same bug pointing the other way.
    /// </summary>
    [Fact]
    public void Begin_transaction_is_a_statement_not_a_block()
    {
        const string sql = "begin transaction; update Orders set Freight = 1; commit";

        var spans = StatementSplitter.Split(Ss, sql);

        Assert.Equal(3, spans.Count);
        Assert.Equal("begin transaction", spans[0].Text);
        Assert.Equal("commit", spans[2].Text);
    }

    /// <summary>
    /// <c>CASE … END</c> has to count as a block for the same reason: there is one <c>END</c> token for
    /// both, so a <c>CASE</c> left uncounted would close a block that was never opened and put the split
    /// back inside the next body.
    /// </summary>
    [Fact]
    public void A_case_expression_does_not_unbalance_the_block_count()
    {
        const string sql = "select case when 1 = 1 then 'a' else 'b' end as c; "
                         + "create procedure p as begin select 1; select 2; end";

        var spans = StatementSplitter.Split(Ss, sql);

        Assert.Equal(2, spans.Count);
        Assert.Equal("select case when 1 = 1 then 'a' else 'b' end as c", spans[0].Text);
        Assert.Equal("create procedure p as begin select 1; select 2; end", spans[1].Text);
    }

    /// <summary>A <c>CASE</c> inside a body keeps the body whole, and the body's own <c>END</c> still
    /// closes it.</summary>
    [Fact]
    public void A_case_inside_a_body_is_counted_and_closed()
    {
        const string sql = "create procedure p as begin select case when 1 = 1 then 1 end; select 2; end; select 9";

        var spans = StatementSplitter.Split(Ss, sql);

        Assert.Equal(2, spans.Count);
        Assert.Equal("select 9", spans[1].Text);
    }

    /// <summary>
    /// A body still being typed leaves the block open, and the whole buffer is then one statement — which
    /// is what Run should send. Mid-edit text has to produce an answer rather than a failure (§1.2), and
    /// this is the honest one.
    /// </summary>
    [Fact]
    public void An_unclosed_block_keeps_the_rest_of_the_buffer_with_it()
    {
        const string sql = "create procedure p as begin select 1; select 2;";

        var span = Assert.Single(StatementSplitter.Split(Ss, sql));
        Assert.Equal("create procedure p as begin select 1; select 2;", span.Text);
    }

    /// <summary>
    /// A <c>GO</c> splits regardless: it is a client directive that ends the whole batch and cannot appear
    /// inside a block, so an unbalanced <c>BEGIN</c> before one is text mid-edit rather than a body that
    /// continues past it. Forgetting the block there is what leaves the following statements runnable.
    /// </summary>
    [Fact]
    public void Go_ends_a_batch_even_with_a_block_left_open()
    {
        const string sql = "create procedure p as begin select 1\nGO\nselect 2; select 3";

        var spans = StatementSplitter.Split(Ss, sql);

        Assert.Equal(3, spans.Count);
        Assert.Equal("create procedure p as begin select 1", spans[0].Text);
        Assert.Equal("select 2", spans[1].Text);
        Assert.Equal("select 3", spans[2].Text);
    }

    /// <summary>
    /// And Postgres is untouched: <c>begin</c> there is a transaction statement, and a function body is
    /// dollar-quoted — which the PG lexer reads as one token, so it never needed a block count.
    /// </summary>
    [Fact]
    public void The_postgres_lexer_still_splits_its_own_begin_blocks()
    {
        var spans = StatementSplitter.Split(Pg, "begin; update t set x = 1; commit;");

        Assert.Equal(3, spans.Count);
        Assert.StartsWith("begin", spans[0].Text);
    }

    [Fact]
    public void Folding_leaves_the_first_line_of_a_batch_visible()
    {
        var sql = "select 1,\n       2\nfrom a\nGO\nselect 3\nfrom b";

        var first = SqlFolding.ComputeFoldRegions(Ss, sql)[0];

        Assert.Equal(sql.IndexOf('\n'), first.Start);
        Assert.Equal(sql.IndexOf("\nGO", System.StringComparison.Ordinal), first.End);
    }
}
