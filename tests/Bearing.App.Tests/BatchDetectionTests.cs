using Bearing.App.Results;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// What counts as a batch — the line between "pages" and "reads to the end". Asked of each connection's own
/// dialect, so a T-SQL script with GO or no separators at all splits the way its server runs it.
/// </summary>
public class BatchDetectionTests
{
    [Theory]
    [InlineData("select 1")]
    [InlineData("select 1;")]
    [InlineData("select 1;\n-- trailing note\n")]
    [InlineData("  \n select * from t where a = ';'  ")]
    public void A_lone_statement_is_not_a_batch(string sql)
    {
        Assert.False(ResultSetBuilder.IsBatch(PostgresDialect.Instance, sql));
        Assert.False(ResultSetBuilder.IsBatch(SqlServerDialect.Instance, sql));
    }

    [Theory]
    [InlineData("declare @from datetime2 = '2026-11-12T18:00:00';\nselect * from t where d >= @from;")]
    [InlineData("select 1;\nselect 2;")]
    [InlineData("select 1\ngo\nselect 2")]
    public void A_script_is_a_batch_on_sql_server(string sql)
        => Assert.True(ResultSetBuilder.IsBatch(SqlServerDialect.Instance, sql));

    [Fact]
    public void Two_statements_are_a_batch_on_postgres()
        => Assert.True(ResultSetBuilder.IsBatch(PostgresDialect.Instance, "set search_path = x;\nselect 1;"));
}
