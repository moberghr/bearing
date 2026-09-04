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

    [Fact]
    public void Folding_leaves_the_first_line_of_a_batch_visible()
    {
        var sql = "select 1,\n       2\nfrom a\nGO\nselect 3\nfrom b";

        var first = SqlFolding.ComputeFoldRegions(Ss, sql)[0];

        Assert.Equal(sql.IndexOf('\n'), first.Start);
        Assert.Equal(sql.IndexOf("\nGO", System.StringComparison.Ordinal), first.End);
    }
}
