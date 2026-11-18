using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// <see cref="ISqlDialect.ScanTargets"/> is a default member: an engine with no scan question answers null
/// ("never asked"), which is not the empty list ("asked, nothing reads a table").
/// </summary>
public class DialectScanTargetsTests
{
    public static TheoryData<ISqlDialect> Dialects => new()
    {
        PostgresDialect.Instance,
        SqlServerDialect.Instance,
        MySqlDialect.Instance,
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void An_engine_with_no_scan_question_answers_null(ISqlDialect dialect)
        => Assert.Null(dialect.ScanTargets("select * from orders"));
}
