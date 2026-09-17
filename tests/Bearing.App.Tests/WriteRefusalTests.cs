using System;
using Bearing.App.Results;
using Bearing.App.Services;
using Bearing.Core.Data;
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
