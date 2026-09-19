using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The SQL Server table of inline-edit expressions (#149, #84). The point of these is the boundary between
/// the two engines' vocabularies: a table per dialect exists so that a cell drawn as "the server will
/// evaluate this" is drawn that way only where the server can.
/// </summary>
public class TSqlEditExpressionTests
{
    [Theory]
    [InlineData("getdate()", "getdate()")]
    [InlineData("GETDATE( )", "getdate()")]
    [InlineData("sysdatetimeoffset()", "sysdatetimeoffset()")]
    [InlineData("CURRENT_TIMESTAMP", "current_timestamp")]
    [InlineData("newid()", "newid()")]
    [InlineData("default", "default")]
    public void A_known_tsql_expression_is_recognized_and_canonically_spelled(string typed, string sql)
        => Assert.Equal(sql, TSqlEditExpression.TryRecognize(typed));

    /// <summary>The important half. <c>now()</c> is not T-SQL, so a cell holding it stays refused and stays
    /// amber — it is emphatically <b>not</b> translated to <c>getdate()</c>, because the statement that runs
    /// has to be the one the cell shows.</summary>
    [Theory]
    [InlineData("now()")]
    [InlineData("gen_random_uuid()")]
    [InlineData("clock_timestamp()")]
    [InlineData("current_database()")]
    public void A_postgres_expression_is_not_recognized_on_sql_server(string typed)
        => Assert.Null(TSqlEditExpression.TryRecognize(typed));

    /// <summary>…and the reverse, so neither table is quietly a superset of the other.</summary>
    [Theory]
    [InlineData("getdate()")]
    [InlineData("newid()")]
    [InlineData("suser_sname()")]
    public void A_tsql_expression_is_not_recognized_on_postgres(string typed)
        => Assert.Null(EditExpression.TryRecognize(typed));

    /// <summary>What the two genuinely share, spelled the same way by both engines.</summary>
    [Theory]
    [InlineData("default")]
    [InlineData("current_timestamp")]
    [InlineData("current_user")]
    [InlineData("session_user")]
    public void The_common_expressions_are_recognized_by_both(string typed)
    {
        Assert.Equal(typed, TSqlEditExpression.TryRecognize(typed));
        Assert.Equal(typed, EditExpression.TryRecognize(typed));
    }

    /// <summary>Nothing in the table takes an argument, which is what stops one carrying a subquery or a
    /// second statement. Asserted rather than assumed, as the Postgres table's own suite does.</summary>
    [Fact]
    public void Nothing_in_the_table_takes_an_argument()
    {
        foreach (var sql in TSqlEditExpression.All)
        {
            var open = sql.IndexOf('(');
            if (open < 0) continue;                       // a niladic function: no parentheses at all
            Assert.Equal("()", sql[open..]);
        }
    }

    /// <summary>The menu's list and the recogniser cannot disagree on this engine either: every offered
    /// item has to be a string the recogniser accepts and hands back unchanged, or a click would stage a
    /// cell the save then refuses.</summary>
    [Fact]
    public void Every_offered_tsql_expression_is_one_the_recogniser_accepts_unchanged()
    {
        Assert.NotEmpty(TSqlEditExpression.Offered);
        foreach (var offered in TSqlEditExpression.Offered)
        {
            Assert.Equal(offered, TSqlEditExpression.TryRecognize(offered));
            Assert.Contains(offered, TSqlEditExpression.All);
        }
    }

    /// <summary>The two engines' menus are not the same menu — which is the point of asking the connection's
    /// dialect for one rather than holding a list in the view.</summary>
    [Fact]
    public void Each_dialect_offers_its_own_engines_spelling()
    {
        Assert.Contains("getdate()", SqlServerDialect.Instance.OfferedEditExpressions);
        Assert.DoesNotContain("now()", SqlServerDialect.Instance.OfferedEditExpressions);

        Assert.Contains("now()", PostgresDialect.Instance.OfferedEditExpressions);
        Assert.DoesNotContain("getdate()", PostgresDialect.Instance.OfferedEditExpressions);

        // …and each offers a subset of what it accepts, never something it would then refuse.
        foreach (var dialect in new ISqlDialect[] { PostgresDialect.Instance, SqlServerDialect.Instance })
            foreach (var offered in dialect.OfferedEditExpressions)
                Assert.Equal(offered, dialect.TryEditExpression(offered));
    }

    /// <summary>The dialect is the seam the app asks through, so the table has to be reachable that way —
    /// and the two dialects must not answer each other's question.</summary>
    [Fact]
    public void The_dialect_hands_over_its_own_table()
    {
        Assert.Equal("getdate()", SqlServerDialect.Instance.TryEditExpression("GetDate()"));
        Assert.Null(SqlServerDialect.Instance.TryEditExpression("now()"));

        Assert.Equal("now()", PostgresDialect.Instance.TryEditExpression("NOW()"));
        Assert.Null(PostgresDialect.Instance.TryEditExpression("getdate()"));
    }
}
