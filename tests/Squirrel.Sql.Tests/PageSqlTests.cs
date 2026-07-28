using Squirrel.Sql;
using Xunit;

namespace Squirrel.Sql.Tests;

public class PageSqlTests
{
    [Theory]
    [InlineData("select * from film")]
    [InlineData("select * from film order by film_id")]
    [InlineData("with recent as (select * from rental) select * from recent")]
    public void Page_uses_a_top_level_suffix_for_a_safe_select(string sql)
    {
        var paged = PageSql.Page(sql, offset: 200, limit: 100);
        Assert.Equal("limit 100 offset 200", paged.Split('\n')[^1]); // top-level suffix, not a wrap
        Assert.DoesNotContain("_sq", paged);
    }

    [Theory]
    [InlineData("select * from film limit 10")]           // its own LIMIT
    [InlineData("select * from film for update")]         // locking clause
    [InlineData("select 1; select 2")]                    // multi-statement batch
    public void Page_falls_back_to_the_wrap_for_an_unsafe_select(string sql)
    {
        var paged = PageSql.Page(sql, offset: 200, limit: 100);
        Assert.StartsWith("select * from (", paged);
        Assert.EndsWith(") as _sq offset 200 limit 100", paged);
    }

    [Fact]
    public void Wrap_pages_a_derived_table_and_strips_a_trailing_semicolon()
    {
        var wrapped = PageSql.Wrap("select * from film;", offset: 50, limit: 25);
        Assert.Equal("select * from (\nselect * from film\n) as _sq offset 50 limit 25", wrapped);
    }
}
