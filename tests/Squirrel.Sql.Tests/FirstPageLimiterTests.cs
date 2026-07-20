using Squirrel.Sql;
using Xunit;

namespace Squirrel.Sql.Tests;

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
}
