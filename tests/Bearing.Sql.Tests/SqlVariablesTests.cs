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
    private static readonly ISqlDialect My = MySqlDialect.Instance;

    public static TheoryData<string> Dialects => new() { "pg", "ms", "my" };

    private static ISqlDialect For(string id) => id switch { "pg" => Pg, "my" => My, _ => Ms };

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

    [Theory]
    [InlineData("select ':id' as x")]                               // in a string
    [InlineData("select \":id\" as x")]                             // double quotes delimit a string too
    [InlineData("select 'it\\'s :id'")]                             // backslash-escaped quote, still inside
    [InlineData("select 1 # :id")]                                  // hash comment
    [InlineData("select 1 -- :id")]
    [InlineData("select 1 /* :id */")]
    [InlineData("select `:id` from t")]                             // in a backticked name
    [InlineData("select @id, @@sql_mode")]                          // MySQL's own variables
    [InlineData("select @n := 1")]                                  // assignment
    [InlineData("set @n:=1")]
    [InlineData("create procedure p() begin l1:loop leave l1; end loop; end")]   // label flush to its loop
    [InlineData("select a : b")]
    public void MySql_colons_that_already_mean_something_are_not_variables(string sql)
        => Assert.Empty(SqlVariables.Find(My, sql));

    [Fact]
    public void A_variable_next_to_a_cast_is_still_found()
        => Assert.Equal(["id"], SqlVariables.Names(Pg, "select :id::int"));

    [Theory]
    [MemberData(nameof(Dialects))]
    public void References_become_named_driver_placeholders_and_values_never_enter_the_text(string dialect)
    {
        var p = SqlVariables.Parameterize(For(dialect), "select * from film where film_id = :ID and :id > 0 and t = :'title'");

        Assert.Equal("select * from film where film_id = @bearing_id and @bearing_id > 0 and t = @bearing_title", p.Sql);
        Assert.Equal(["ID", "title"], p.Names);                  // as first written, once each
        Assert.Contains("title", p.QuotedNames);
        Assert.DoesNotContain("id", p.QuotedNames);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Sql_without_variables_is_returned_unchanged(string dialect)
    {
        const string sql = "select ':id', 1 -- :id";
        var p = SqlVariables.Parameterize(For(dialect), sql);

        Assert.Same(sql, p.Sql);
        Assert.Empty(p.Names);
    }
}
