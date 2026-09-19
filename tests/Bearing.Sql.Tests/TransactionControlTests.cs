using System.Collections.Generic;
using System.Linq;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Which statements each dialect calls transaction control (#131). Manual-commit mode refuses these, so the
/// classification decides what a user is stopped from running — and the two engines genuinely disagree about
/// one word, which is the whole reason this is per dialect rather than one shared list.
/// </summary>
public class TransactionControlTests
{
    private static string? Pg(string sql) => Control(PostgresDialect.Instance, sql);
    private static string? TSql(string sql) => Control(SqlServerDialect.Instance, sql);

    /// <summary>Through the guard rather than through the dialect method directly: what the refusal reads is
    /// <c>StatementRisk.TransactionControl</c>, and the wiring between the two is as easy to get wrong as
    /// the classification.</summary>
    private static string? Control(ISqlDialect dialect, string sql)
        => WriteGuard.Describe(dialect, sql).Single().TransactionControl;

    [Theory]
    [InlineData("begin", "BEGIN")]
    [InlineData("BEGIN;", "BEGIN")]
    [InlineData("start transaction", "START")]
    [InlineData("commit", "COMMIT")]
    [InlineData("end", "END")]                      // Postgres' own synonym for commit
    [InlineData("rollback", "ROLLBACK")]
    [InlineData("abort", "ABORT")]
    [InlineData("savepoint s1", "SAVEPOINT")]
    [InlineData("release savepoint s1", "RELEASE")]
    public void Postgres_names_its_transaction_statements(string sql, string expected)
        => Assert.Equal(expected, Pg(sql));

    [Theory]
    [InlineData("select 1")]
    [InlineData("insert into t values (1)")]
    [InlineData("update t set a = 1")]
    public void An_ordinary_statement_is_not_transaction_control(string sql)
    {
        Assert.Null(Pg(sql));
        Assert.Null(TSql(sql));
    }

    /// <summary>
    /// <c>PREPARE TRANSACTION</c> is two-phase commit; a bare <c>PREPARE</c> is a prepared statement and has
    /// nothing to do with transactions. Reading only the first word would refuse every prepared statement on
    /// a manual-commit connection.
    /// </summary>
    [Fact]
    public void Postgres_reads_the_second_word_of_prepare()
    {
        Assert.Equal("PREPARE TRANSACTION", Pg("prepare transaction 'tx1'"));
        Assert.Null(Pg("prepare p (int) as select $1"));
    }

    /// <summary>
    /// The one word the two engines disagree about, and the reason this is not a translation: in T-SQL
    /// <c>BEGIN</c> opens a <c>BEGIN … END</c> block. Classifying it as Postgres does would refuse every
    /// T-SQL block on a manual-commit connection — a stored-procedure body, an <c>IF</c>, a <c>WHILE</c>.
    /// </summary>
    [Fact]
    public void A_bare_T_SQL_BEGIN_is_a_block_and_not_a_transaction()
    {
        Assert.Null(TSql("begin select 1 end"));
        Assert.Equal("BEGIN", Pg("begin"));
    }

    [Theory]
    [InlineData("begin tran", "BEGIN TRANSACTION")]
    [InlineData("begin transaction", "BEGIN TRANSACTION")]
    [InlineData("BEGIN DISTRIBUTED TRANSACTION", "BEGIN TRANSACTION")]
    [InlineData("commit", "COMMIT")]
    [InlineData("commit transaction", "COMMIT")]
    [InlineData("rollback", "ROLLBACK")]
    [InlineData("rollback tran", "ROLLBACK")]
    [InlineData("save transaction s1", "SAVE TRANSACTION")]
    public void T_SQL_names_its_own(string sql, string expected)
        => Assert.Equal(expected, TSql(sql));

    /// <summary>T-SQL has no <c>END</c>-as-commit: it closes a block. Treating it as a commit would mean a
    /// manual-commit connection refused the end of every block it had already allowed the start of.</summary>
    [Fact]
    public void T_SQL_END_closes_a_block_rather_than_a_transaction()
        => Assert.Null(TSql("end"));

    /// <summary>
    /// The classification is orthogonal to the risk verdict and must not have moved it. §1.2 does not allow
    /// the guard to be narrowed, and the T-SQL guard's conservative default has always reported a bare
    /// COMMIT as risky — that behaviour is pinned here rather than quietly improved alongside this feature.
    /// </summary>
    [Fact]
    public void Classifying_a_statement_does_not_change_whether_it_is_risky()
    {
        Assert.False(WriteGuard.Describe(PostgresDialect.Instance, "commit").Single().IsRisky);
        Assert.True(WriteGuard.Describe(SqlServerDialect.Instance, "commit").Single().IsRisky);
    }

    /// <summary>A batch is classified statement by statement, because the refusal names what it found.</summary>
    [Fact]
    public void Each_statement_in_a_batch_is_classified_on_its_own()
    {
        var described = WriteGuard.Describe(PostgresDialect.Instance, "insert into t values (1); commit;");

        Assert.Equal(2, described.Count);
        Assert.Null(described[0].TransactionControl);
        Assert.Equal("COMMIT", described[1].TransactionControl);
        Assert.Equal(["COMMIT"], described.Where(s => s.IsTransactionControl).Select(s => s.TransactionControl!));
    }
}
