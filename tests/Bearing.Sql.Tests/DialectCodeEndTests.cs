using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// <see cref="ISqlDialect.CodeEnd"/>: where a statement's code ends on screen, past whitespace and comments —
/// what inline results hang under. Each engine answers with its own lexer.
/// </summary>
public class DialectCodeEndTests
{
    [Theory]
    [InlineData("select 1;", 9)]
    [InlineData("select 1;\n\n-- header\n\n", 9)]
    [InlineData("select 1; /* note */  ", 9)]
    [InlineData("select '-- not a comment';", 26)]
    [InlineData("-- only a comment\n", null)]
    [InlineData("   ", null)]
    public void Postgres_ends_at_the_last_token_that_is_code(string sql, int? expected)
        => Assert.Equal(expected, PostgresDialect.Instance.CodeEnd(sql));

    [Theory]
    [InlineData("select 1;", 9)]
    [InlineData("select 1 -- trailing\n", 8)]
    [InlineData("select [a;b] /* x */", 12)]
    [InlineData("/* only */", null)]
    public void Sql_server_ends_at_the_last_token_that_is_code(string sql, int? expected)
        => Assert.Equal(expected, SqlServerDialect.Instance.CodeEnd(sql));

    [Theory]
    [InlineData("select 1;", 9)]
    [InlineData("select 1 # note\n", 8)]
    [InlineData("select 1;\n\n-- header\n\n", 9)]
    [InlineData("select 1; /* note */  ", 9)]
    [InlineData("select '# not a comment';", 25)]
    [InlineData("select `a#b`", 12)]
    [InlineData("select 1 /*!50000 , 2 */ # x", 24)]
    [InlineData("delimiter $$\nselect 1$$\ndelimiter ;\n", 23)]
    [InlineData("# only a comment\n", null)]
    [InlineData("   ", null)]
    public void MySql_ends_at_the_last_token_that_is_code(string sql, int? expected)
        => Assert.Equal(expected, MySqlDialect.Instance.CodeEnd(sql));
}
