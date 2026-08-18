using Bearing.Core.Schema;
using Xunit;

namespace Bearing.Sql.Tests;

public class AliasResolverTests
{
    private static string Alias(string tableName, params string[] taken)
        => AliasResolver.Determine(new TableInfo(1, "public", tableName, RelationKind.Table), taken);

    [Theory]
    [InlineData("film", "f")]
    [InlineData("film_actor", "fa")]
    [InlineData("customer_address_line", "cal")]
    [InlineData("MigrationHistory", "mh")]
    [InlineData("__MigrationHistory", "mh")]          // was "_": split on '_' left a single part
    [InlineData("__EFMigrationsHistory", "emh")]
    [InlineData("orderLine", "ol")]
    [InlineData("Orders", "o")]
    [InlineData("__", "t")]                            // no letters at all
    [InlineData("", "t")]
    public void Alias_is_the_initials_of_the_words_in_the_name(string name, string expected)
        => Assert.Equal(expected, Alias(name));

    [Fact]
    public void Collisions_get_a_numeric_suffix()
    {
        Assert.Equal("f2", Alias("film", "f"));
        Assert.Equal("f3", Alias("film", "f", "f2"));
    }

    [Fact]
    public void Collision_check_ignores_case()
        => Assert.Equal("mh2", Alias("__MigrationHistory", "MH"));

    [Fact]
    public void Generated_aliases_never_need_quoting()
    {
        foreach (var name in new[] { "__MigrationHistory", "Orders", "order", "film_actor", "2fast" })
            Assert.False(PgIdentifier.NeedsQuoting(Alias(name)));
    }
}
