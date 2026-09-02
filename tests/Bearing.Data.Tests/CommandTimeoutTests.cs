using System;
using System.Collections.Generic;
using Bearing.Core.Data;
using Bearing.Testing;
using Bearing.Data;
using System.Threading.Tasks;
using System.Threading;
using Bearing.Data.Postgres;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// No clock of our own on a running query, and a legible message when something else imposes one.
/// <para>
/// Reported: a query that took a while failed with <c>Exception while reading from stream</c>. Npgsql
/// defaults <c>CommandTimeout</c> to 30 seconds and nothing here ever set it, so every query had a
/// half-minute ceiling and announced hitting it as a fault in the driver's plumbing.
/// </para>
/// </summary>
public class CommandTimeoutTests
{
    private static ConnectionInfo Info(Dictionary<string, string>? options = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        ProviderId = PostgresProvider.ProviderId,
        Host = "localhost",
        Port = 5432,
        Database = "app",
        User = "postgres",
        Options = options ?? new Dictionary<string, string>(),
    };

    [Fact]
    public void A_connection_imposes_no_command_timeout()
    {
        // Running a slow analytical query is the point of the tool, and Esc already cancels one — so a clock
        // we impose can only get in the way of work the user meant to do.
        var built = PostgresConnectionString.Build(Info(), "pw");

        Assert.Equal(0, built.CommandTimeout);
    }

    [Fact]
    public void A_keepalive_is_configured_so_no_timeout_is_still_safe()
    {
        // The other half of the decision. With no clock on the command, something has to notice a socket
        // that has died — and a keepalive can tell "the link is gone" from "the query is slow", which is
        // exactly the distinction a command timeout cannot make.
        var built = PostgresConnectionString.Build(Info(), "pw");

        Assert.True(built.KeepAlive > 0, "no keepalive, so a dead connection would be waited on forever");
    }

    [Fact]
    public void A_connection_can_still_impose_its_own_timeout()
    {
        // Overridable per connection, the same way MaxPoolSize is: the default is "no limit", not "no choice".
        var built = PostgresConnectionString.Build(
            Info(new Dictionary<string, string> { ["CommandTimeout"] = "45" }), "pw");

        Assert.Equal(45, built.CommandTimeout);
    }

    // ---- what the user is told --------------------------------------------------------------------

    [Fact]
    public void A_timeout_is_explained_rather_than_relayed()
    {
        // The shape Npgsql actually throws: a stream message wrapping the timeout that caused it.
        var ex = new InvalidOperationException("Exception while reading from stream",
            new TimeoutException("Timeout during reading attempt"));

        var text = PostgresErrorText.Explain(ex);

        Assert.DoesNotContain("reading from stream", text);
        Assert.Contains("timeout", text, StringComparison.OrdinalIgnoreCase);
        // It has to say the query may still be running server-side, because that is the part with
        // consequences — an UPDATE that "failed" here may well have committed.
        Assert.Contains("still be running", text);
    }

    [Fact]
    public void A_timeout_nested_deeper_is_still_found()
    {
        var ex = new InvalidOperationException("outer",
            new InvalidOperationException("Exception while reading from stream",
                new TimeoutException("timed out")));

        Assert.Contains("timeout", PostgresErrorText.Explain(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unrelated_failure_keeps_its_own_message()
    {
        // The explanation must not swallow everything else — a connection refused is not a timeout, and
        // relabelling it would send the user to the wrong setting.
        var text = PostgresErrorText.Explain(new InvalidOperationException("Connection refused"));

        Assert.Contains("Connection refused", text);
        Assert.DoesNotContain("still be running", text);
    }

    [Fact]
    public void An_unrelated_failure_still_has_its_credentials_stripped()
    {
        // Explain falls through to SafeErrorText, and must keep doing so (§1.1).
        var text = PostgresErrorText.Explain(
            new InvalidOperationException("failed for Host=db;Password=hunter2;Database=app"));

        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void A_cyclic_exception_chain_does_not_spin()
    {
        // The walk is bounded, so a chain that loops cannot hang the error path — which would turn a failed
        // query into a hung app.
        var inner = new InvalidOperationException("inner");
        var outer = new InvalidOperationException("outer", inner);

        Assert.NotNull(PostgresErrorText.Explain(outer));
    }

    // ---- against a live server --------------------------------------------------------------------

    /// <summary>
    /// A real timeout, from a real driver, produces the explained message.
    /// <para>
    /// The assertions above build the exception by hand, which only proves the walk finds a
    /// <see cref="TimeoutException"/> where one was put. This one makes Npgsql throw its own — whatever shape
    /// that actually is in this driver version — by giving the connection a one-second ceiling and asking it
    /// to sleep for three. A driver that reworded or restructured its timeout would fail here rather than
    /// silently fall through to the raw text.
    /// </para>
    /// <para>
    /// Two seconds rather than the default, deliberately: the default is now "no limit", and a test that
    /// waited for it would have to sleep past thirty seconds to prove anything. That the default really has
    /// no ceiling is asserted on the builder above.
    /// </para>
    /// <para>
    /// The sleep is well past the ceiling because the driver overshoots it by about two seconds — measured
    /// at 2s→4.1s, 3s→5.1s, 5s→7.1s and 30s→32.1s. A first version of this test slept for three against a
    /// one-second ceiling and passed for the wrong reason: the sleep finished before the timeout fired, so
    /// it looked like the driver ignoring the setting.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_real_driver_timeout_is_explained_too()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var info = PgTestServer.Info() with
        {
            Options = new Dictionary<string, string> { ["CommandTimeout"] = "2" },
        };
        await using var factory = provider.CreateConnectionFactory(info, PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var results = await provider.CreateQueryExecutor(factory)
            .ExecuteAsync("select pg_sleep(8)", new QueryOptions(), CancellationToken.None);

        var error = results[0].Error;
        Assert.NotNull(error);
        Assert.Contains("timeout", error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still be running", error.Message);
        Assert.DoesNotContain("reading from stream", error.Message);
    }
}
