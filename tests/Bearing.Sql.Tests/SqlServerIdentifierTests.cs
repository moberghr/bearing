using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The rule that must NOT be the Postgres rule. Postgres folds unquoted identifiers to lower case, so
/// <see cref="PgIdentifier"/> quotes anything that isn't already lower case; SQL Server preserves case,
/// so applying that rule here would bracket every PascalCase name in a real catalog.
/// </summary>
public class SqlServerIdentifierTests
{
    [Theory]
    [InlineData("Customers")]         // the headline case: PascalCase must stay bare
    [InlineData("OrderDetails")]
    [InlineData("film")]
    [InlineData("film_actor")]
    [InlineData("_private")]
    [InlineData("T$x")]
    [InlineData("mail@home")]         // @ and # are legal *inside* a regular identifier
    [InlineData("tmp#1")]
    public void Regular_identifiers_are_left_bare(string name)
    {
        Assert.False(SqlServerIdentifier.NeedsQuoting(name));
        Assert.Equal(name, SqlServerIdentifier.QuoteIfNeeded(name));
    }

    [Theory]
    [InlineData("select", "[select]")]              // reserved keyword
    [InlineData("Order", "[Order]")]                // reserved, and reservation is case-insensitive
    [InlineData("USER", "[USER]")]
    [InlineData("2fast", "[2fast]")]                // leading digit
    [InlineData("with space", "[with space]")]
    [InlineData("a-b", "[a-b]")]                    // not a regular-identifier character
    [InlineData("@var", "[@var]")]                  // a leading @ would parse as a variable
    [InlineData("#temp", "[#temp]")]                // a leading # would parse as a temp table
    [InlineData("", "[]")]
    public void Names_that_would_not_round_trip_are_bracketed(string name, string expected)
    {
        Assert.True(SqlServerIdentifier.NeedsQuoting(name));
        Assert.Equal(expected, SqlServerIdentifier.QuoteIfNeeded(name));
    }

    [Fact]
    public void A_closing_bracket_is_escaped_by_doubling_it()
        => Assert.Equal("[we]]ird]", SqlServerIdentifier.Quote("we]ird"));

    [Fact]
    public void Quoting_is_unconditional_even_for_a_name_that_would_survive_bare()
        => Assert.Equal("[Customers]", SqlServerIdentifier.Quote("Customers"));

    [Theory]
    [InlineData("Customers")]
    [InlineData("we]ird")]
    [InlineData("]]")]
    [InlineData("with space")]
    [InlineData("")]
    public void Quote_and_Unquote_round_trip(string name)
        => Assert.Equal(name, SqlServerIdentifier.Unquote(SqlServerIdentifier.Quote(name)));

    [Fact]
    public void Unquote_also_reads_the_double_quoted_form_a_script_may_use()
    {
        // QUOTED_IDENTIFIER is ON for every client this app could be, so a T-SQL script may delimit
        // with " as well as [ ].
        Assert.Equal("Customers", SqlServerIdentifier.Unquote("\"Customers\""));
        Assert.Equal("we\"ird", SqlServerIdentifier.Unquote("\"we\"\"ird\""));
    }

    [Fact]
    public void Unquote_leaves_an_undelimited_name_alone()
        => Assert.Equal("Customers", SqlServerIdentifier.Unquote("Customers"));
}
