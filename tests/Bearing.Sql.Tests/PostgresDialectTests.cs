using Bearing.Core.Data;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// <see cref="PostgresDialect"/> is a facade, and the point of these is that it stays one: every string
/// it serves has to be the same string the Postgres-named static already produced, or the app would have
/// two sources of truth for one query and only one of them under test.
/// </summary>
public class PostgresDialectTests
{
    private static readonly ISqlDialect Pg = PostgresDialect.Instance;

    [Fact]
    public void Id_matches_the_provider_id()
        => Assert.Equal("postgres", Pg.Id);

    [Theory]
    [InlineData("film")]
    [InlineData("Users")]
    [InlineData("order")]
    [InlineData("we\"ird")]
    [InlineData("")]
    public void Identifier_rules_are_PgIdentifiers(string name)
    {
        Assert.Equal(PgIdentifier.Quote(name), Pg.Quote(name));
        Assert.Equal(PgIdentifier.QuoteIfNeeded(name), Pg.QuoteIfNeeded(name));
        Assert.Equal(PgIdentifier.NeedsQuoting(name), Pg.NeedsQuoting(name));
        Assert.Equal(PgIdentifier.Unquote(PgIdentifier.Quote(name)), Pg.Unquote(Pg.Quote(name)));
    }

    [Theory]
    [InlineData("select * from film")]
    [InlineData("select * from film order by film_id")]
    [InlineData("select * from film limit 10")]
    [InlineData("select 1; select 2")]
    public void Paging_is_the_FirstPageLimiter_and_PageSql_text(string sql)
    {
        Assert.Equal(FirstPageLimiter.TryAppendPage(sql, 200, 100), Pg.TryAppendPage(sql, 200, 100));
        Assert.Equal(PageSql.Wrap(sql, 200, 100), Pg.Wrap(sql, 200, 100));
        Assert.Equal(PageSql.Page(sql, 200, 100), PageSql.Page(Pg, sql, 200, 100));
    }

    [Fact]
    public void The_count_wrap_is_the_shape_the_executor_has_always_sent()
    {
        // Byte-for-byte the string PostgresQueryExecutor.CountAsync builds. It lives here so a second
        // engine can vary it; if these ever disagree, the total-row count is being generated twice.
        Assert.Equal("select count(*) from (\nselect * from film\n) as _sq", Pg.CountWrap("select * from film;"));
    }

    [Fact]
    public void Insert_still_ends_with_returning_star()
    {
        var cmd = DmlGenerator.Insert(Pg, "public", "language", new[] { new ColumnValue("name", "Klingon") });
        Assert.Equal("insert into \"public\".\"language\" (\"name\") values (@p0) returning *", cmd.Sql);
        Assert.Equal(cmd.Sql, DmlGenerator.Insert("public", "language", new[] { new ColumnValue("name", "Klingon") }).Sql);
    }
}
