using System.Collections.Generic;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// <c>:name</c> script variables, per dialect: what counts as one, and what the script reads like once
/// they are filled in. The negatives matter more than the positives — every one of them is a colon that
/// already means something, and prompting for it would make a working query unrunnable.
/// </summary>
public class SqlVariablesTests
{
    private static readonly ISqlDialect Ms = SqlServerDialect.Instance;
    private static readonly ISqlDialect Pg = PostgresDialect.Instance;

    public static TheoryData<string> Dialects => new() { "pg", "ms" };

    private static ISqlDialect For(string id) => id == "pg" ? Pg : Ms;

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Names_are_found_in_order_and_once_each(string dialect)
        => Assert.Equal(
            ["id", "since"],
            SqlVariables.Names(For(dialect), "select * from t where id = :id and d > :since or parent = :ID"));

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Keywords_are_good_names(string dialect)
        => Assert.Equal(["date", "user"], SqlVariables.Names(For(dialect), "select :date, :user"));

    [Theory]
    [InlineData("select 1::int")]                                   // cast
    [InlineData("select '2024-01-01'::date")]
    [InlineData("select ':id' as x")]                               // in a string
    [InlineData("select 1 -- where id = :id")]                      // in a line comment
    [InlineData("select 1 /* :id */")]                              // in a block comment
    [InlineData("select \":id\" from t")]                           // in a quoted name
    [InlineData("do $$ begin x := 1; perform :id; end $$")]         // in a dollar-quoted body
    [InlineData("select a[1:n] from t")]                            // array slice
    [InlineData("select a[lo:hi][1:2] from t")]
    [InlineData("select a : b")]                                    // colon not touching the name
    [InlineData("select $1")]                                       // a driver placeholder isn't ours
    public void Postgres_colons_that_already_mean_something_are_not_variables(string sql)
        => Assert.Empty(SqlVariables.Find(Pg, sql));

    [Theory]
    [InlineData("grant select on schema::dbo to someone")]          // scope qualifier, both halves
    [InlineData("select ':id' as x")]
    [InlineData("select N':id' as x")]
    [InlineData("select 1 -- :id")]
    [InlineData("select 1 /* /* :id */ */")]                        // nested block comment
    [InlineData("select [:id] from t")]                             // in a delimited name
    [InlineData("select @id")]                                      // T-SQL's own variables
    [InlineData("select a : b")]
    public void T_sql_colons_that_already_mean_something_are_not_variables(string sql)
        => Assert.Empty(SqlVariables.Find(Ms, sql));

    [Fact]
    public void A_variable_next_to_a_cast_is_still_found()
        => Assert.Equal(["id"], SqlVariables.Names(Pg, "select :id::int"));

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Values_are_spliced_in_as_typed(string dialect)
    {
        var values = new Dictionary<string, string> { ["id"] = "42", ["tbl"] = "public.film" };

        Assert.Equal(
            "select * from public.film where film_id = 42 and 42 > 0",
            SqlVariables.Substitute(For(dialect), "select * from :tbl where film_id = :ID and :id > 0", values));
    }

    [Fact]
    public void The_quoted_form_becomes_a_string_literal_in_each_dialect()
    {
        var values = new Dictionary<string, string> { ["name"] = "O'Brien" };

        Assert.Equal("where n = 'O''Brien'", SqlVariables.Substitute(Pg, "where n = :'name'", values));
        Assert.Equal("where n = N'O''Brien'", SqlVariables.Substitute(Ms, "where n = :'name'", values));
    }

    [Fact]
    public void Plain_and_quoted_references_to_one_name_are_one_variable()
    {
        Assert.Equal(["name"], SqlVariables.Names(Pg, "select :name, :'name'"));
        Assert.Equal(
            "select x, 'x'",
            SqlVariables.Substitute(Pg, "select :name, :'name'", new Dictionary<string, string> { ["name"] = "x" }));
    }

    [Fact]
    public void A_reference_with_no_value_is_left_for_the_server_to_report()
        => Assert.Equal(
            "select 1, :b",
            SqlVariables.Substitute(Pg, "select :a, :b", new Dictionary<string, string> { ["a"] = "1" }));

    [Fact]
    public void A_value_is_never_rescanned_for_variables()
        => Assert.Equal(
            "select ':b', 2",
            SqlVariables.Substitute(
                Pg, "select :a, :b", new Dictionary<string, string> { ["a"] = "':b'", ["b"] = "2" }));
}
