using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The fixed table of expressions an inline edit may stand for (#149). The tests that matter here are the
/// negative ones: what the recogniser refuses is what keeps §5.4's parameterization rule broken in one
/// bounded place rather than generally.
/// </summary>
public class EditExpressionTests
{
    [Theory]
    [InlineData("now()", "now()")]
    [InlineData("NOW()", "now()")]
    [InlineData("  now( )  ", "now()")]
    [InlineData("Now ()", "now()")]
    [InlineData("current_timestamp", "current_timestamp")]
    [InlineData("CURRENT_DATE", "current_date")]
    [InlineData("gen_random_uuid()", "gen_random_uuid()")]
    [InlineData("default", "default")]
    public void A_known_expression_is_recognized_and_canonically_spelled(string typed, string sql)
        => Assert.Equal(sql, EditExpression.TryRecognize(typed));

    /// <summary>The emitted SQL comes from the table, never from the input — which is what makes it safe to
    /// interpolate at all. A recognised spelling and its canonical form are not always the same string.</summary>
    [Fact]
    public void The_emitted_sql_is_the_tables_spelling_not_the_users()
    {
        Assert.Equal("now()", EditExpression.TryRecognize("NOW (  )"));
        Assert.All(EditExpression.All, sql => Assert.Equal(sql, EditExpression.TryRecognize(sql)));
    }

    [Theory]
    [InlineData("nextval('s')")]          // takes an argument — and has a side effect
    [InlineData("now();drop table t")]
    [InlineData("now() + interval '1 day'")]
    [InlineData("(select max(id) from t)")]
    [InlineData("amount * 1.1")]          // arithmetic over a column: a different feature (#149)
    [InlineData("foo()")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_is_not_an_expression(string? typed)
        => Assert.Null(EditExpression.TryRecognize(typed));

    /// <summary>Nothing in the table takes an argument, so nothing in it can carry a subquery or a second
    /// statement. That is a property of the list rather than of the parser, and this is what keeps it one.</summary>
    [Fact]
    public void No_known_expression_takes_an_argument()
        => Assert.All(EditExpression.All, sql =>
            Assert.True(!sql.Contains('(') || sql.EndsWith("()"), $"'{sql}' takes an argument"));
}
