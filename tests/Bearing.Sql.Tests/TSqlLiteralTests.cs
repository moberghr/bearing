using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The two lexical questions about literals that are asked outside the completion engine, per dialect.
/// Both were answered with the PostgreSQL lexer on every dialect until <see cref="TSqlLiterals"/> existed,
/// and both were measurably wrong on T-SQL — the two <c>…_used_to_…</c> tests below are those measurements,
/// kept as tests so the regressions cannot come back quietly.
/// </summary>
public class TSqlLiteralTests
{
    private static readonly ISqlDialect Ms = SqlServerDialect.Instance;
    private static readonly ISqlDialect Pg = PostgresDialect.Instance;

    // ---- Is the caret inside a string? ----

    [Fact]
    public void An_apostrophe_in_a_delimited_name_used_to_swallow_the_rest_of_the_buffer()
    {
        // The bug, and why it was total rather than cosmetic: this check is the FIRST thing
        // CompletionEngine.Complete does, so a false positive returns no suggestions for every caret after
        // it. The PG lexer has no [bracketed] identifier, so the ' opened a string that never closed —
        // and names like [O'Donnell] are ordinary in real schemas.
        const string sql = "select * from [O'Donnell] o where o.";

        Assert.False(Ms.InStringLiteral(sql, sql.Length));
        // Postgres' answer is deliberately unchanged: `[` is not a delimiter there, so for that dialect the
        // apostrophe really does open a literal. Each engine is right about its own syntax.
        Assert.True(Pg.InStringLiteral(sql, sql.Length));
    }

    [Theory]
    [InlineData("select * from Orders where Name = 'x", true)]      // genuinely unterminated
    [InlineData("select * from Orders where Name = 'ada'", true)]   // just past the closing quote
    [InlineData("select * from t where name = 'ada' and x.", false)]// past a closed string
    [InlineData("select * from [Order Details] o where o.", false)]// delimited name, no apostrophe
    [InlineData("select N'text", true)]                             // the N prefix is part of the literal
    public void A_caret_inside_a_t_sql_string_is_recognised(string sql, bool inside)
        => Assert.Equal(inside, Ms.InStringLiteral(sql, sql.Length));

    [Fact]
    public void The_opening_quote_itself_is_not_inside_anything_yet()
    {
        // Matching the Postgres rule: at the position where a literal begins the caret has not entered it,
        // so completion still works there.
        const string sql = "select * from t where n = '";
        Assert.False(Ms.InStringLiteral(sql, sql.IndexOf('\'')));
        Assert.True(Ms.InStringLiteral(sql, sql.Length));
    }

    // ---- What the query log stores ----

    [Fact]
    public void A_binary_literal_used_to_survive_redaction()
    {
        // §1.3: with QueryLogRedactLiterals on, the value was kept in the log the setting promised to strip.
        // The PG lexer read the `0` as a number and `xDEADBEEF` as an identifier, so only the zero went.
        const string sql = "select * from T where Blob = 0xDEADBEEF and Name = N'Ada'";

        Assert.Equal("select * from T where Blob = ? and Name = N'?'", Ms.RedactLiterals(sql));
        Assert.Contains("xDEADBEEF", Pg.RedactLiterals(sql));   // the measured before-state, on PG's own lexer
    }

    [Theory]
    // A quoted literal keeps its shell — the quoting is shape, and a redacted statement should still read
    // as SQL. The N prefix is part of that shell.
    [InlineData("select * from T where Name = 'Ada'", "select * from T where Name = '?'")]
    [InlineData("select * from T where Name = N'Ada'", "select * from T where Name = N'?'")]
    // Numeric and binary literals go entirely.
    [InlineData("select * from T where Id = 42", "select * from T where Id = ?")]
    [InlineData("select * from T where Amount = 99.5", "select * from T where Amount = ?")]
    [InlineData("select * from T where Blob = 0xFF", "select * from T where Blob = ?")]
    // An unterminated quote keeps its opener rather than inventing a closer.
    [InlineData("select * from T where Name = 'Ad", "select * from T where Name = '?")]
    public void A_literal_value_is_replaced_and_its_shape_is_kept(string sql, string expected)
        => Assert.Equal(expected, Ms.RedactLiterals(sql));

    [Fact]
    public void An_identifier_is_structure_and_is_never_redacted()
    {
        // Delimited or not. A log with its table names removed would not be worth keeping.
        Assert.Equal(
            "select [Salary] from [Order Details] where Amount = ?",
            Ms.RedactLiterals("select [Salary] from [Order Details] where Amount = 99.5"));
    }

    [Fact]
    public void Empty_input_is_not_a_special_case_worth_throwing_over()
    {
        Assert.Equal("", Ms.RedactLiterals(null));
        Assert.Equal("", Ms.RedactLiterals(""));
        Assert.False(Ms.InStringLiteral("", 0));
    }
}
