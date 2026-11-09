using System;
using System.Linq;
using Bearing.App.Results;
using Bearing.App.Services;
using Bearing.Core.Data;
using Bearing.Sessions;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// A read-only connection refuses a write rather than asking about one (#99), and the two SQLSTATEs the
/// safety settings produce are explained in terms of the setting that produced them (#105).
/// <para>
/// Both are derived from the connection record and the write guard's existing verdict, so they are testable
/// without a window or a server. The server-side half — that Postgres actually refuses — is
/// <c>SessionPolicyTests</c> in Bearing.Data.Tests, against a live server.
/// </para>
/// </summary>
public class WriteRefusalTests
{
    private static ConnectionInfo Conn(bool readOnly = false, bool guarded = false, int timeoutSeconds = 0)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "prod-eu",
            ProviderId = "postgres",
            Environment = "Production",
            ReadOnly = readOnly,
            RequireWriteConfirmation = guarded,
            StatementTimeoutSeconds = timeoutSeconds,
        };

    private static string? Refuse(ConnectionInfo conn, string sql)
        => WriteRefusal.Reason(conn, WriteGuard.Describe(sql));

    // ---- what is refused ------------------------------------------------------------------------

    [Theory]
    [InlineData("delete from rental where id = 3")]
    [InlineData("update payment set amount = 0")]
    [InlineData("insert into film (title) values ('x')")]
    [InlineData("truncate payment")]
    [InlineData("drop table film")]
    [InlineData("alter table film add column x int")]
    [InlineData("with moved as (delete from a returning *) insert into b select * from moved")]
    public void A_write_on_a_read_only_connection_is_refused(string sql)
    {
        var refused = Refuse(Conn(readOnly: true), sql);

        Assert.NotNull(refused);
        Assert.Contains("prod-eu", refused);
        Assert.Contains("read-only", refused);
    }

    [Theory]
    [InlineData("select * from film")]
    [InlineData("explain select * from film")]
    [InlineData("show statement_timeout")]
    public void A_read_is_never_refused(string sql)
        => Assert.Null(Refuse(Conn(readOnly: true), sql));

    [Fact]
    public void A_write_on_an_ordinary_connection_is_not_refused()
    {
        // The setting has to be what decides, not the verb: every other connection still runs its writes.
        Assert.Null(Refuse(Conn(), "delete from rental"));
        Assert.Null(Refuse(Conn(guarded: true), "delete from rental"));
    }

    [Fact]
    public void Read_only_refuses_whether_or_not_the_connection_also_confirms()
    {
        // The two settings answer different questions and neither implies the other. A read-only connection
        // that is *not* marked guarded must still refuse — otherwise the safe answer would depend on a
        // second checkbox nobody connected it to.
        Assert.NotNull(Refuse(Conn(readOnly: true, guarded: false), "delete from rental"));
        Assert.NotNull(Refuse(Conn(readOnly: true, guarded: true), "delete from rental"));
    }

    [Fact]
    public void The_refusal_names_the_verbs_it_is_refusing()
    {
        var refused = Refuse(Conn(readOnly: true), "select 1; delete from rental; update film set x = 1;");

        Assert.NotNull(refused);
        Assert.Contains("DELETE", refused);
        Assert.Contains("UPDATE", refused);
    }

    [Fact]
    public void An_inline_save_is_refused_without_needing_a_verdict()
    {
        // The grid's generated DML is a write by construction, so there is no risk verdict to read.
        Assert.NotNull(WriteRefusal.ReasonForEdits(Conn(readOnly: true)));
        Assert.Null(WriteRefusal.ReasonForEdits(Conn()));
    }

    // ---- explaining what came back --------------------------------------------------------------

    private static QueryError Err(string sqlState) => new("canceling statement", sqlState, null);

    [Fact]
    public void A_cancelled_statement_is_blamed_on_the_timeout_that_was_actually_set()
    {
        var explained = QueryErrorText.Explain(Err("57014"), Conn(timeoutSeconds: 30));

        Assert.NotNull(explained);
        Assert.Contains("prod-eu", explained);
        Assert.Contains("30 s", explained);
    }

    [Fact]
    public void A_cancelled_statement_with_no_timeout_configured_does_not_blame_the_timeout()
    {
        // The mistake this guards: 57014 also arrives from pg_cancel_backend and from a statement_timeout set
        // on the server rather than by us. Naming *our* setting for those would be a confident wrong answer
        // about a connection that has no such setting at all.
        var explained = QueryErrorText.Explain(Err("57014"), Conn());

        Assert.NotNull(explained);
        Assert.DoesNotContain("prod-eu", explained);
        Assert.Contains("on the server", explained);
    }

    [Fact]
    public void The_users_own_cancel_is_left_alone()
    {
        // Esc already reports itself, and the run path decides this from its cancellation token. A timeout
        // message on top of a cancel the user asked for would contradict them.
        Assert.Null(QueryErrorText.Explain(Err("57014"), Conn(timeoutSeconds: 30), userCancelled: true));
    }

    [Fact]
    public void A_refused_write_names_the_read_only_setting_when_it_is_ours()
    {
        var explained = QueryErrorText.Explain(Err("25006"), Conn(readOnly: true));

        Assert.NotNull(explained);
        Assert.Contains("prod-eu", explained);
        Assert.Contains("read-only", explained);
    }

    [Fact]
    public void A_mysql_read_only_refusal_names_the_setting_too()
    {
        // The MySQL executor records the error number (1792), not a SQLSTATE.
        var explained = QueryErrorText.Explain(Err("1792"), Conn(readOnly: true) with { ProviderId = "mysql" });

        Assert.NotNull(explained);
        Assert.Contains("prod-eu", explained);
        Assert.Contains("read-only", explained);
    }

    private static string? ManualCommitRefusal(string providerId, ISqlDialect dialect, string sql, bool held)
        => WriteRefusal.ReasonForTransactionControl(
            Conn() with { ProviderId = providerId, ManualCommit = true }, WriteGuard.Describe(dialect, sql), held);

    [Fact]
    public void Manual_commit_refuses_an_implicit_commit_while_a_transaction_is_held_and_says_why()
    {
        var reason = ManualCommitRefusal("mysql", MySqlDialect.Instance, "lock tables t read", held: true);

        Assert.NotNull(reason);
        Assert.Contains("LOCK not run: it commits implicitly", reason);
        Assert.Contains("would end the transaction Bearing holds", reason);
        Assert.DoesNotContain("use Commit or Rollback to end it", reason);
    }

    [Theory]
    [InlineData("create table x (a int)")]
    [InlineData("alter table t add column b int")]
    [InlineData("lock tables t read")]
    [InlineData("drop table t")]
    public void Manual_commit_runs_an_implicit_commit_alone_with_nothing_held(string sql)
        => Assert.Null(ManualCommitRefusal("mysql", MySqlDialect.Instance, sql, held: false));

    [Theory]
    [InlineData("insert into t values (1); create table x (a int)")]
    [InlineData("create table x (a int); update t set a = 1")]
    public void Manual_commit_refuses_an_implicit_commit_beside_a_write(string sql)
    {
        var reason = ManualCommitRefusal("mysql", MySqlDialect.Instance, sql, held: false);

        Assert.NotNull(reason);
        Assert.Contains("CREATE not run", reason);
        Assert.Contains("other writes in this batch", reason);
    }

    /// <summary>
    /// A block MariaDB runs as it is sent, holding a write and a statement that commits implicitly: it opens no
    /// transaction (it commits implicitly), so its INSERT would commit without Commit. Refused with nothing held,
    /// as the same two statements written separately are (PR #180 confirmation review).
    /// </summary>
    [Theory]
    [InlineData("begin not atomic insert into t values (1); create table x (a int); end")]
    [InlineData("lbl: begin not atomic update t set a = 1; drop table y; end")]
    [InlineData("if 1 then insert into t values (1); create table x (a int); end if")]
    [InlineData("while @n > 0 do delete from t; alter table t add column b int; set @n = 0; end while")]
    public void Manual_commit_refuses_a_block_that_mixes_a_write_with_an_implicit_commit(string sql)
    {
        var reason = ManualCommitRefusal("mysql", MySqlDialect.Instance, sql, held: false);

        Assert.NotNull(reason);
        Assert.Contains("inside a block that also writes", reason);
        Assert.DoesNotContain("use Commit or Rollback to end it", reason);
    }

    /// <summary>A stored program's body is not run when it is created, so its writes commit nothing then:
    /// creating one with nothing held still runs (skajfes's PR #180 thread).</summary>
    [Theory]
    [InlineData("create procedure p() begin insert into t values (1); create table x (a int); end")]
    [InlineData("create trigger tr after insert on t for each row begin insert into log values (1); end")]
    public void Manual_commit_still_creates_a_stored_program_whose_body_writes(string sql)
        => Assert.Null(ManualCommitRefusal("mysql", MySqlDialect.Instance, sql, held: false));

    /// <summary>A block of DDL alone, or of writes alone, is not the mixed case: the first commits only itself,
    /// the second opens a transaction like any write.</summary>
    [Theory]
    [InlineData("begin not atomic create table x (a int); drop table y; end")]
    [InlineData("begin not atomic insert into t values (1); delete from t where a = 2; end")]
    public void Manual_commit_runs_a_block_that_does_not_mix(string sql)
        => Assert.Null(ManualCommitRefusal("mysql", MySqlDialect.Instance, sql, held: false));

    [Theory]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("start transaction", false)]
    [InlineData("begin not atomic create table x (a int); commit; end", false)]
    public void Manual_commit_still_refuses_typed_control_on_mysql_whatever_is_held(string sql, bool held)
    {
        var reason = ManualCommitRefusal("mysql", MySqlDialect.Instance, sql, held);

        Assert.NotNull(reason);
        Assert.Contains("use Commit or Rollback to end it", reason);
    }

    [Theory]
    [InlineData("create table x (a int)")]
    [InlineData("insert into t values (1)")]
    public void Opening_a_transaction_skips_a_statement_that_commits_implicitly(string sql)
    {
        var statement = WriteGuard.Describe(MySqlDialect.Instance, sql).Single();
        Assert.Equal(!statement.CommitsImplicitly, WriteRefusal.OpensTransaction(statement));
        Assert.Equal(sql.StartsWith("insert", StringComparison.Ordinal), WriteRefusal.OpensTransaction(statement));
    }

    [Theory]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    [InlineData("sqlserver", false)]
    public void Postgres_and_sql_server_ddl_is_untouched_by_manual_commit(string providerId, bool held)
    {
        // Their DDL is transactional (or, on SQL Server, at least does not commit an open transaction), so
        // nothing about it is transaction control.
        ISqlDialect dialect = providerId == "postgres" ? PostgresDialect.Instance : SqlServerDialect.Instance;
        Assert.Null(ManualCommitRefusal(providerId, dialect, "create table x (a int)", held));
        Assert.NotNull(ManualCommitRefusal(providerId, dialect, "commit", held));
    }

    [Fact]
    public void A_mysql_timeout_names_the_connections_own_setting()
    {
        var explained = QueryErrorText.Explain(Err("-1"), Conn(timeoutSeconds: 30) with { ProviderId = "mysql" });

        Assert.NotNull(explained);
        Assert.Contains("prod-eu", explained);
        Assert.Contains("statement timeout", explained);
        Assert.Contains("KILL QUERY", explained);
        Assert.DoesNotContain("pg_cancel_backend", explained);
    }

    [Fact]
    public void A_mysql_timeout_with_no_bearing_timeout_blames_the_options_not_us()
    {
        var explained = QueryErrorText.Explain(Err("-1"), Conn() with { ProviderId = "mysql" });

        Assert.NotNull(explained);
        Assert.DoesNotContain("prod-eu's statement timeout", explained);
        Assert.Contains("options", explained);
    }

    [Theory]
    [InlineData("1317", "KILL QUERY")]
    [InlineData("3024", "max_execution_time")]
    [InlineData("1969", "max_statement_time")]
    public void A_mysql_server_side_stop_says_so_in_mysql_terms(string number, string expected)
    {
        var explained = QueryErrorText.Explain(Err(number), Conn(timeoutSeconds: 30) with { ProviderId = "mysql" });

        Assert.NotNull(explained);
        Assert.Contains(expected, explained);
        Assert.DoesNotContain("pg_cancel_backend", explained);
        Assert.DoesNotContain("prod-eu's statement timeout", explained);
    }

    [Fact]
    public void A_mysql_users_own_cancel_is_not_explained()
        => Assert.Null(QueryErrorText.Explain(Err("-1"), Conn(timeoutSeconds: 30) with { ProviderId = "mysql" }, userCancelled: true));

    [Theory]
    [InlineData("-1")]
    [InlineData("1317")]
    public void Mysql_error_numbers_mean_nothing_on_postgres(string number)
        => Assert.Null(QueryErrorText.Explain(Err(number), Conn(timeoutSeconds: 30)));

    [Fact]
    public void Error_number_1792_means_read_only_only_on_mysql()
        // SQL Server records error numbers as well, and its 1792 is something else entirely.
        => Assert.Null(QueryErrorText.Explain(Err("1792"), Conn(readOnly: true) with { ProviderId = "sqlserver" }));

    [Fact]
    public void A_refused_write_on_a_connection_we_did_not_mark_claims_no_cause()
    {
        // A read-only *server*, a role with no write privileges, or an enclosing read-only transaction all
        // produce this too. Nothing here checked which, so nothing here says (§1.1).
        var explained = QueryErrorText.Explain(Err("25006"), Conn());

        Assert.NotNull(explained);
        Assert.DoesNotContain("prod-eu", explained);
        Assert.Contains("transaction is read-only", explained);
    }

    [Theory]
    [InlineData("42601")]
    [InlineData("23505")]
    [InlineData(null)]
    public void Every_other_error_keeps_the_servers_own_words(string? sqlState)
    {
        // Null means "leave the existing text alone". This layer speaks only for the two states a connection
        // setting explains; a syntax error explained by us would be a worse message than the server's.
        var error = sqlState is null ? null : Err(sqlState);
        Assert.Null(QueryErrorText.Explain(error, Conn(readOnly: true, timeoutSeconds: 30)));
    }
}
