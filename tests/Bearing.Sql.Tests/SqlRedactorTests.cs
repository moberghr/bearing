using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Stripping literal values out of logged SQL (#22). The query log holds the statements you ran verbatim,
/// which means it holds whatever was in the WHERE clause — an email, a national id, a customer name — in
/// plain text until retention prunes it.
/// <para>
/// Lexer-based rather than regex, because a regex cannot tell a quote inside a dollar-quoted body from one
/// that ends a string; getting that wrong either leaves the value in the log or eats the rest of the
/// statement. Most of what follows is that distinction.
/// </para>
/// </summary>
public class SqlRedactorTests
{
    private static string R(string sql) => SqlRedactor.Redact(sql);

    // ---- the values go ---------------------------------------------------------------------------

    [Fact]
    public void A_string_value_is_replaced()
        => Assert.Equal(
            "select * from users where email = '?'",
            R("select * from users where email = 'ada@example.com'"));

    [Fact]
    public void A_number_is_replaced_too()
    {
        // An id, a salary, an account number — as personal as a name, and the shape of the query survives
        // either way.
        Assert.Equal("select * from t where id = ?", R("select * from t where id = 4711"));
        Assert.Equal("update t set rate = ? where id = ?", R("update t set rate = 0.075 where id = 12"));
    }

    [Fact]
    public void Several_values_in_one_statement_all_go()
        => Assert.Equal(
            "insert into p (name, ssn, age) values ('?', '?', ?)",
            R("insert into p (name, ssn, age) values ('Ada', '123-45-6789', 36)"));

    [Fact]
    public void A_quote_inside_a_value_does_not_end_it_early()
    {
        // The classic escape: '' is a literal quote, not the end of the string. A scan that stops at the
        // second quote leaves the rest of the name in the log.
        Assert.Equal("select * from t where name = '?'", R("select * from t where name = 'O''Brien'"));
        Assert.DoesNotContain("Brien", R("select * from t where name = 'O''Brien'"));
    }

    [Fact]
    public void An_escape_string_goes_whole()
    {
        var redacted = R(@"select * from t where note = E'line\nbreak: ada@example.com'");

        Assert.DoesNotContain("ada@example.com", redacted);
        Assert.Contains("'?'", redacted);
    }

    [Fact]
    public void A_dollar_quoted_body_becomes_one_placeholder()
    {
        // Three tokens — open tag, text, close tag — collapsing to one. Replacing only the text would leave
        // $$'?'$$, which is not what a placeholder should look like, and $tag$ bodies are where a whole
        // function definition (or a pasted data dump) lives.
        Assert.Equal("select '?'", R("select $$ada@example.com$$"));
        Assert.Equal("select '?'", R("select $tag$ada@example.com$tag$"));
        Assert.DoesNotContain("example.com", R("select $tag$ 'quoted' and $$nested$$ text $tag$"));
    }

    [Fact]
    public void An_unterminated_string_is_still_redacted()
    {
        // The case a naive scan mishandles, and it still holds a value — a statement that failed to parse is
        // logged too.
        var redacted = R("select * from t where email = 'ada@example.com");

        Assert.DoesNotContain("ada@example.com", redacted);
    }

    [Fact]
    public void A_value_inside_a_comment_is_left_alone()
    {
        // Comments are prose the user wrote, not data the statement carried. Rewriting them would mangle a
        // statement people read back, and a comment is not where the WHERE clause's values are.
        Assert.Equal("-- for ada@example.com\nselect ?", R("-- for ada@example.com\nselect 1"));
    }

    // ---- the shape stays ------------------------------------------------------------------------

    [Fact]
    public void The_statement_is_still_recognisable()
    {
        // The point of redaction rather than deletion: you can still see what you did.
        var redacted = R("""
            select u.name, count(*)
            from users u join orders o on o.user_id = u.id
            where u.created_at > '2024-01-01' and o.total > 100
            group by u.name
            """);

        Assert.Contains("users u join orders o", redacted);
        Assert.Contains("group by u.name", redacted);
        Assert.DoesNotContain("2024-01-01", redacted);
        Assert.DoesNotContain("100", redacted);
    }

    [Fact]
    public void Identifiers_keywords_and_formatting_survive_untouched()
    {
        // Quoted identifiers are *not* string literals, however much they look like them: redacting them
        // would leave a statement that names no tables.
        var sql = "SELECT \"Ada's Column\"\n  FROM \"Users\"\n WHERE id = 1;";

        Assert.Equal("SELECT \"Ada's Column\"\n  FROM \"Users\"\n WHERE id = ?;", R(sql));
    }

    [Fact]
    public void A_statement_with_no_literals_comes_back_identical()
    {
        // Returned unchanged, so a caller can redact unconditionally.
        const string sql = "select * from users order by created_at desc";

        Assert.Same(sql, R(sql) is var result && result == sql ? sql : result);
        Assert.Equal(sql, R(sql));
    }

    [Fact]
    public void A_multi_statement_batch_is_redacted_throughout()
        => Assert.Equal(
            "update t set a = '?'; delete from u where id = ?;",
            R("update t set a = 'secret'; delete from u where id = 9;"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_nothing_out(string? sql)
        => Assert.Equal("", SqlRedactor.Redact(sql));

    [Fact]
    public void A_placeholder_is_visibly_a_placeholder()
    {
        // Not 0 and not '': a redacted statement must not read as one that really did ask for zero or for the
        // empty string.
        Assert.Equal("?", SqlRedactor.NumberPlaceholder);
        Assert.Equal("'?'", SqlRedactor.StringPlaceholder);
    }

    [Fact]
    public void Parameter_markers_are_not_literals()
    {
        // Already parameterised SQL has nothing to redact, and $1 must not be mistaken for a dollar-quoted
        // string — which would swallow everything up to the next $.
        Assert.Equal("select * from t where id = $1 and name = $2", R("select * from t where id = $1 and name = $2"));
    }

    [Fact]
    public void What_it_does_not_claim_to_do()
    {
        // Not anonymisation, and the doc comment says so: identifiers carry meaning and are left intact.
        var redacted = R("select ssn from patient_records where mrn = 'X'");

        Assert.Contains("patient_records", redacted);
        Assert.Contains("ssn", redacted);
    }
}
