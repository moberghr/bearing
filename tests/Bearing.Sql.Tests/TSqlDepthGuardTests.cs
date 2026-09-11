using Bearing.Core.Schema;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The stack-overflow backstop, read through the T-SQL grammar.
/// <para>
/// <see cref="CompletionDepthGuardTests"/> covers the same guard on Postgres, and it passed while the
/// guard was dead for SQL Server: the depth was counted with <c>PostgreSQLLexer</c>'s token numbers over
/// whatever stream it was handed, and T-SQL's parenthesis is 1192 where Postgres' is 2 — so a T-SQL buffer
/// reported ~0 nesting however deep it went, and <c>TSqlParser.tsql_file()</c> was handed it anyway. A
/// <see cref="StackOverflowException"/> is not catchable in .NET: the process dies with the buffer in it.
/// </para>
/// <para>
/// If one of these ever kills the test host rather than failing, that is the bug back again.
/// </para>
/// </summary>
public class TSqlDepthGuardTests
{
    private static readonly SchemaSnapshot Schema = TSqlTestSchema.Build();

    private static CompletionEngine SqlServer => new(() => SqlServerDialect.Instance);

    private static string Nested(int depth)
        => $"select {new string('(', depth)}1{new string(')', depth)} from Customers";

    [Theory]
    [InlineData(PgParsing.MaxNestingDepth + 1)]
    [InlineData(5000)]
    [InlineData(50000)]
    public void Deeply_nested_tsql_yields_no_suggestions_rather_than_killing_the_process(int depth)
    {
        var sql = Nested(depth);

        Assert.Empty(SqlServer.Complete(sql, sql.Length, Schema).Suggestions);
        Assert.Empty(SqlServer.IntentsAt(sql, sql.Length));
    }

    /// <summary>
    /// The limit is one number for both grammars, and this is the half of that claim T-SQL has to carry:
    /// a buffer *at* the limit is still parsed, so 1000 sits under this grammar's cliff too rather than
    /// only under PostgreSQL's, where it was measured.
    /// </summary>
    [Fact]
    public void A_buffer_at_the_limit_is_still_parsed()
    {
        var sql = Nested(PgParsing.MaxNestingDepth);

        // No assertion about *what* comes back — the point is that the parse ran and returned at all.
        Assert.False(ParseDepth.TooDeep(TSqlParseRules.Instance, TSqlParseRules.Instance.LexAll(sql)));
        SqlServer.Complete(sql, sql.Length, Schema);
    }

    /// <summary>
    /// The counts the guard is made of. <c>[</c> is absent on purpose: it delimits an identifier in T-SQL
    /// and arrives as one token, so it is not a nesting level — which is also why the Postgres set cannot
    /// simply be reused.
    /// </summary>
    [Theory]
    [InlineData("select a from t", 0)]
    [InlineData("select (a) from t", 1)]
    [InlineData("select ((( a ))) from t", 3)]
    [InlineData("select case when a then 1 end from t", 1)]
    [InlineData("select * from [Order Details]", 0)]
    [InlineData("select [a],[b] from [t]", 0)]
    [InlineData("begin select 1 end", 1)]
    [InlineData("))))) select (a", 1)]
    public void The_depth_is_counted_in_tsqls_own_tokens(string sql, int expected)
        => Assert.Equal(expected, ParseDepth.Of(TSqlParseRules.Instance, TSqlParseRules.Instance.LexAll(sql)));

    /// <summary>The guard must cost ordinary T-SQL completion nothing.</summary>
    [Fact]
    public void Ordinary_nesting_still_completes()
    {
        const string sql = "select * from (select id from Customers) a where ";
        Assert.NotEmpty(SqlServer.IntentsAt(sql, sql.Length));
    }
}
