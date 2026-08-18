using Xunit;

namespace Bearing.Sql.Tests;

public class PgIdentifierTests
{
    [Theory]
    [InlineData("film")]              // the common case must stay bare — quoting everything reads wrong
    [InlineData("film_actor")]
    [InlineData("_private")]
    [InlineData("t$x")]
    [InlineData("name")]              // col_name_keyword: legal as a relation/column name
    [InlineData("value")]             // unreserved_keyword
    public void Plain_lower_case_names_are_left_bare(string name)
    {
        Assert.False(PgIdentifier.NeedsQuoting(name));
        Assert.Equal(name, PgIdentifier.QuoteIfNeeded(name));
    }

    [Theory]
    [InlineData("__MigrationHistory", "\"__MigrationHistory\"")]  // folding would look for __migrationhistory
    [InlineData("Users", "\"Users\"")]
    [InlineData("order", "\"order\"")]                            // reserved_keyword
    [InlineData("user", "\"user\"")]
    [InlineData("left", "\"left\"")]                              // type_func_name_keyword
    [InlineData("2fast", "\"2fast\"")]                            // leading digit
    [InlineData("with space", "\"with space\"")]
    [InlineData("", "\"\"")]
    public void Names_that_would_not_round_trip_are_quoted(string name, string expected)
    {
        Assert.True(PgIdentifier.NeedsQuoting(name));
        Assert.Equal(expected, PgIdentifier.QuoteIfNeeded(name));
    }

    [Fact]
    public void Embedded_quotes_are_doubled()
        => Assert.Equal("\"a\"\"b\"", PgIdentifier.QuoteIfNeeded("a\"b"));

    [Fact]
    public void Unquote_reverses_quote()
    {
        foreach (var name in new[] { "film", "__MigrationHistory", "order", "a\"b", "with space" })
            Assert.Equal(name, PgIdentifier.Unquote(PgIdentifier.Quote(name)));
    }

    [Fact]
    public void Unquote_leaves_a_bare_name_alone()
        => Assert.Equal("film", PgIdentifier.Unquote("film"));
}
