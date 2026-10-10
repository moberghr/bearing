using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The appended clause and the derived-table wrap both need the statement without its terminator — and
/// "the text ends in <c>;</c>" was the wrong test for that: a trailing <c>-- COMMIT;</c> made the
/// comment's semicolon the one that got stripped, and the first page went out as <c>…; limit 101</c>.
/// </summary>
public class StatementEndTests
{
    /// <summary>The shape of the script that failed: terminator, blank line, commented-out COMMIT.</summary>
    private const string Script = """
        -- Eyeball, then COMMIT (or ROLLBACK).
        select l.id, l.amount
        from report_line l
        join report r on r.id = l.report_id
        where r.name = 'x;y'
        order by l.id;

        -- COMMIT;
        """;

    private const string Body = """
        -- Eyeball, then COMMIT (or ROLLBACK).
        select l.id, l.amount
        from report_line l
        join report r on r.id = l.report_id
        where r.name = 'x;y'
        order by l.id
        """;

    [Theory]
    [InlineData("select 1;", "select 1")]
    [InlineData("select 1;;  \n", "select 1")]
    [InlineData("select 1 -- note", "select 1")]
    [InlineData("select 1; /* done; */", "select 1")]
    [InlineData("select ';'", "select ';'")]
    [InlineData("select 1", "select 1")]
    public void Postgres_statements_lose_their_terminator_and_trailing_comments(string sql, string expected)
        => Assert.Equal(expected, StatementEnd.TrimPostgres(sql));

    [Theory]
    [InlineData("select 1;\n-- go;", "select 1")]
    [InlineData("select [a;b] from t; /* x; */", "select [a;b] from t")]
    [InlineData("select N';'", "select N';'")]
    public void T_sql_statements_lose_their_terminator_and_trailing_comments(string sql, string expected)
        => Assert.Equal(expected, StatementEnd.TrimTSql(sql));

    [Fact]
    public void The_first_page_limit_lands_after_the_statement_not_after_its_terminator()
        => Assert.Equal(Body + "\nlimit 101", FirstPageLimiter.TryAppendLimit(Script, 101));

    [Fact]
    public void The_paging_and_count_wraps_hold_the_statement_without_its_terminator()
    {
        Assert.Equal($"select * from (\n{Body}\n) as _sq offset 0 limit 10", PageSql.Wrap(Script, 0, 10));
        Assert.Equal($"select count(*) from (\n{Body}\n) as _sq", PageSql.CountWrap(Script));
    }

    [Fact]
    public void Sql_server_fetch_lands_after_the_statement_not_after_its_terminator()
        => Assert.Equal(
            "select id from t order by id\noffset 0 rows fetch next 10 rows only",
            SqlServerDialect.Instance.TryAppendPage("select id from t order by id;\n-- COMMIT;", 0, 10));

    [Fact]
    public void A_trailing_line_comment_cannot_comment_out_the_measured_explains_terminator()
        => Assert.Equal(
            "BEGIN;\nEXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) select 1;\nROLLBACK;",
            ExplainSql.Measured("select 1 -- just checking").Sql);
}
