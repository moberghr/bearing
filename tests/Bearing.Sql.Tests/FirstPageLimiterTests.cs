using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

public class FirstPageLimiterTests
{
    private const int N = 101;

    [Theory]
    [InlineData("select * from film")]
    [InlineData("select * from film order by film_id")]
    [InlineData("SELECT * FROM film")]                                  // case-insensitive
    [InlineData("select * from film;")]                                 // trailing semicolon
    [InlineData("select * from film -- newest first")]                  // trailing line comment
    [InlineData("with recent as (select * from rental) select * from recent")]
    [InlineData("select a from t1 union select a from t2")]             // set op: limit binds to the whole
    [InlineData("select * from t where id in (select id from other limit 5)")] // inner LIMIT is not top-level
    public void Appends_limit_for_a_single_read_only_select(string sql)
    {
        var limited = FirstPageLimiter.TryAppendLimit(sql, N);
        Assert.NotNull(limited);
        Assert.EndsWith($"\nlimit {N}", limited);
        Assert.DoesNotContain(";", limited);                            // terminator stripped before suffix
    }

    [Theory]
    [InlineData("select * from film limit 10")]                         // already capped
    [InlineData("select * from film LIMIT 10")]
    [InlineData("select * from film fetch first 10 rows only")]         // FETCH form
    [InlineData("select * from film for update")]                       // locking clause must follow LIMIT
    [InlineData("select * into backup from film")]                      // table-creating write in disguise
    [InlineData("select 1; select 2")]                                  // multi-statement batch
    [InlineData("update film set title = 'x' where film_id = 1")]       // write
    [InlineData("insert into t values (1)")]                            // write
    [InlineData("with moved as (delete from t returning *) select * from moved")] // data-modifying CTE
    [InlineData("explain select * from film")]                          // not a plain SELECT/WITH
    [InlineData("table film")]                                          // conservative: not handled
    [InlineData("")]
    [InlineData("   ")]
    public void Leaves_unsafe_or_already_capped_queries_alone(string sql)
        => Assert.Null(FirstPageLimiter.TryAppendLimit(sql, N));

    [Fact]
    public void Non_positive_limit_is_rejected()
    {
        Assert.Null(FirstPageLimiter.TryAppendLimit("select * from film", 0));
        Assert.Null(FirstPageLimiter.TryAppendLimit("select * from film", -1));
    }

    [Fact]
    public void Appended_limit_is_on_its_own_line_so_a_trailing_comment_cannot_swallow_it()
    {
        var limited = FirstPageLimiter.TryAppendLimit("select * from film -- note", N);
        Assert.NotNull(limited);
        // The limit sits after a newline, past the end of the comment line.
        var lastLine = limited!.Split('\n')[^1];
        Assert.Equal($"limit {N}", lastLine);
    }

    // ---- TryAppendPage (paging: same top-level suffix on every page) ------------------------------

    [Theory]
    [InlineData("select * from film")]
    [InlineData("select * from film order by film_id")]
    [InlineData("select * from film order by film_id -- newest first")]      // suffix clears the comment
    [InlineData("select * from film;")]                                      // trailing semicolon
    [InlineData("with recent as (select * from rental) select * from recent")]
    public void Appends_limit_and_offset_for_a_safe_select(string sql)
    {
        var paged = FirstPageLimiter.TryAppendPage(sql, offset: 200, limit: 100);
        Assert.NotNull(paged);
        Assert.Equal("limit 100 offset 200", paged!.Split('\n')[^1]); // on its own line, past any comment
        Assert.DoesNotContain(";", paged);
    }

    [Fact]
    public void Offset_zero_omits_the_offset_clause_so_the_first_page_matches_TryAppendLimit()
    {
        const string sql = "select * from film order by film_id";
        var page0 = FirstPageLimiter.TryAppendPage(sql, offset: 0, limit: N);
        Assert.Equal($"{sql}\nlimit {N}", page0);
        Assert.Equal(FirstPageLimiter.TryAppendLimit(sql, N), page0); // identical to the first-page path
    }

    [Theory]
    [InlineData("select * from film limit 10")]                              // already capped
    [InlineData("select * from film for update")]                            // locking clause
    [InlineData("select 1; select 2")]                                       // multi-statement batch
    [InlineData("update film set title = 'x' where film_id = 1")]            // write
    [InlineData("with moved as (delete from t returning *) select * from moved")] // data-modifying CTE
    [InlineData("explain select * from film")]                               // not a plain SELECT/WITH
    [InlineData("")]
    public void Leaves_unsafe_queries_unpaged(string sql)
        => Assert.Null(FirstPageLimiter.TryAppendPage(sql, offset: 100, limit: 100));

    [Fact]
    public void Negative_offset_or_non_positive_limit_is_rejected()
    {
        Assert.Null(FirstPageLimiter.TryAppendPage("select * from film", offset: -1, limit: 100));
        Assert.Null(FirstPageLimiter.TryAppendPage("select * from film", offset: 100, limit: 0));
        Assert.Null(FirstPageLimiter.TryAppendPage("select * from film", offset: 100, limit: -5));
    }
}
