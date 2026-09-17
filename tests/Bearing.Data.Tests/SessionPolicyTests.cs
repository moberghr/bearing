using Bearing.Core.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Npgsql;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The two per-connection safety settings applied to a session rather than a statement: read-only (#99) and a
/// server-side statement timeout (#105).
/// <para>
/// Both ride the <b>startup packet</b>, and that is the thing worth pinning. A <c>SET</c> would have reached
/// only the physical connection it was issued on — a pool holds up to ten, every read path opens one
/// directly, and they are pruned and reopened under a live session — so a read-only connection would have
/// been read-only or not depending on which socket a statement landed on. The live tests at the bottom are
/// what actually prove the packet arrives; everything above them only proves what we sent.
/// </para>
/// </summary>
public class SessionPolicyTests
{
    private static ConnectionInfo Info(
        bool readOnly = false, int timeoutSeconds = 0, Dictionary<string, string>? options = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "prod",
            ProviderId = "postgres",
            Host = "db.example.com",
            Database = "app",
            User = "u",
            ReadOnly = readOnly,
            StatementTimeoutSeconds = timeoutSeconds,
            Options = options ?? new Dictionary<string, string>(),
        };

    private static string? Built(ConnectionInfo info)
        => PostgresConnectionString.Build(info, "pw").Options;

    // ---- the fields reach the driver ------------------------------------------------------------

    [Fact]
    public void A_connection_with_neither_setting_sends_no_startup_options()
    {
        // The default has to be "nothing", not "-c" anything: this setting arrived after every project file
        // on disk, and a packet on a connection nobody configured would change how it already behaved.
        Assert.Null(PostgresConnectionString.StartupOptionsFor(Info()));
        Assert.Null(Built(Info()));
    }

    [Fact]
    public void Read_only_asks_the_server_to_refuse_writes()
        => Assert.Equal("-c default_transaction_read_only=on", Built(Info(readOnly: true)));

    [Fact]
    public void The_timeout_reaches_the_server_in_milliseconds()
    {
        // Milliseconds because that is the GUC's own unit when no suffix is given, so nothing depends on
        // Postgres parsing a unit out of a startup value. The server normalizes it back — see the live test.
        Assert.Equal("-c statement_timeout=30000", Built(Info(timeoutSeconds: 30)));
    }

    [Fact]
    public void Both_settings_travel_together()
        => Assert.Equal("-c default_transaction_read_only=on -c statement_timeout=5000",
            Built(Info(readOnly: true, timeoutSeconds: 5)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_timeout_that_is_not_a_duration_is_no_timeout(int seconds)
    {
        // A clock that has already run out is not a setting anyone meant, and cancelling every statement is
        // a worse answer than imposing no limit.
        Assert.Equal(SessionPolicy.NoTimeout, SessionPolicy.TimeoutSeconds(Info(timeoutSeconds: seconds)));
        Assert.Null(Built(Info(timeoutSeconds: seconds)));
    }

    [Theory]
    [InlineData(3_000_000)]
    [InlineData(int.MaxValue)]
    public void A_timeout_too_large_for_the_server_is_clamped_rather_than_overflowed(int seconds)
    {
        // project.json is hand-editable, so these values are reachable. statement_timeout is milliseconds in
        // an int, so seconds * 1000 wrapped negative and Postgres refused the startup value — which does not
        // mean "no timeout", it means the connection stops opening at all. A silly number must cost the
        // timeout, never the connection.
        var built = Built(Info(timeoutSeconds: seconds));

        Assert.Equal($"-c statement_timeout={SessionPolicy.MaxTimeoutSeconds * 1000}", built);
        Assert.DoesNotContain("-", built!["-c statement_timeout=".Length..]);
    }

    // ---- one source of truth --------------------------------------------------------------------

    [Fact]
    public void The_options_bag_cannot_outrank_the_fields()
    {
        // The threat this closes: project.json is shared, and the startup packet is a single keyword rather
        // than a merge — so a bag entry would not add to what we composed, it would replace it, and a
        // read-only production connection would come back writable.
        var info = Info(readOnly: true, options: new Dictionary<string, string>
        {
            ["Options"] = "-c default_transaction_read_only=off",
        });

        Assert.Equal("-c default_transaction_read_only=on", Built(info));
    }

    [Fact]
    public void The_bag_entry_is_dropped_in_whatever_case_it_arrived_in()
    {
        foreach (var key in new[] { "options", "OPTIONS", "Options" })
        {
            var info = Info(readOnly: true, options: new Dictionary<string, string> { [key] = "-c whatever=1" });
            Assert.Equal("-c default_transaction_read_only=on", Built(info));
        }
    }

    [Fact]
    public void The_reserved_keyword_probe_can_actually_fail()
    {
        // An anti-tautology guard for the two tests above. If "Options" were not a real Npgsql keyword they
        // would pass by hitting the "not a driver keyword, ignore" arm rather than the reserved one, and the
        // reservation could be deleted without either noticing. This asserts the keyword exists and does
        // reach the driver when nothing reserves it — i.e. that there is something to reserve.
        var csb = new NpgsqlConnectionStringBuilder();
        Assert.True(csb.ContainsKey("Options"));

        csb["Options"] = "-c statement_timeout=1";
        Assert.Equal("-c statement_timeout=1", csb.Options);
    }

    [Fact]
    public void Other_options_still_reach_the_driver()
    {
        // Reserving the packet must not turn the bag off: it is still where MaxPoolSize and CommandTimeout
        // are overridden per connection.
        var info = Info(readOnly: true, options: new Dictionary<string, string> { ["MaxPoolSize"] = "3" });
        var csb = PostgresConnectionString.Build(info, "pw");

        Assert.Equal(3, csb.MaxPoolSize);
        Assert.Equal("-c default_transaction_read_only=on", csb.Options);
    }

    [Fact]
    public void A_connection_whose_safety_settings_changed_is_not_the_same_connection()
    {
        // Not a connection-string question but the same setting's: the session manager reuses a live pool
        // while the record still matches, and these settings are fixed when the pool is built. Without them
        // in that comparison, a connection the user just marked read-only would keep serving writes from the
        // pool it already had — the #23 bug with a destructive statement at the end of it. SameConnection and
        // SameNetwork compare these two values, so this asserts they are what actually differ.
        var before = Info();

        Assert.NotEqual(
            SessionPolicy.IsReadOnly(before),
            SessionPolicy.IsReadOnly(before with { ReadOnly = true }));
        Assert.NotEqual(
            SessionPolicy.TimeoutSeconds(before),
            SessionPolicy.TimeoutSeconds(before with { StatementTimeoutSeconds = 30 }));
        Assert.NotEqual(
            SessionPolicy.TimeoutSeconds(before with { StatementTimeoutSeconds = 30 }),
            SessionPolicy.TimeoutSeconds(before with { StatementTimeoutSeconds = 60 }));

        // And that the packet follows them, so the two comparisons cannot drift apart.
        Assert.NotEqual(
            PostgresConnectionString.StartupOptionsFor(before),
            PostgresConnectionString.StartupOptionsFor(before with { ReadOnly = true }));
    }

    [Fact]
    public void The_two_timeouts_are_different_controls()
    {
        // 0.5.3 (#93) set CommandTimeout to 0 so Bearing stops imposing a clock on how long it waits. This
        // setting is the other half: the server's limit on how long it *runs*. Setting one must not move the
        // other, or "no client timeout" would quietly become "no limit anywhere".
        var built = PostgresConnectionString.Build(Info(timeoutSeconds: 30), "pw");

        Assert.Equal(0, built.CommandTimeout);
        Assert.Equal("-c statement_timeout=30000", built.Options);
    }

    // ---- what the settings say about themselves -------------------------------------------------

    [Fact]
    public void An_absent_timeout_is_never_printed_as_a_number()
    {
        // §1.7: an absence is typed, not rendered as zero. "0 s" would read as a limit of no time at all.
        Assert.Equal("no limit", SessionPolicy.TimeoutLabel(Info()));
        Assert.Equal("30 s", SessionPolicy.TimeoutLabel(Info(timeoutSeconds: 30)));
    }

    [Fact]
    public void A_connection_with_no_safety_settings_is_not_reassured_about()
    {
        // Empty rather than "nothing is set": the dialog hides the note, instead of printing a sentence
        // whose effect is to make an unguarded production connection look considered.
        Assert.Equal("", SessionPolicy.Advice(Info()));
    }

    [Fact]
    public void Read_only_is_described_as_stopping_mistakes_and_not_as_prevention()
    {
        // The honesty requirement, and the reason this is asserted rather than left to review:
        // default_transaction_read_only is USERSET, so a SET lifts it. Describing it as prevention would be
        // the same overclaim as calling TlsMode.Require "verified".
        var advice = SessionPolicy.Advice(Info(readOnly: true));

        Assert.Contains("refuses writes", advice);
        Assert.Contains("SET", advice);
        Assert.Contains("privileges", advice);
    }

    [Fact]
    public void Every_combination_of_the_two_can_say_what_it_arranged()
    {
        foreach (var info in new[]
                 {
                     Info(readOnly: true),
                     Info(timeoutSeconds: 30),
                     Info(readOnly: true, timeoutSeconds: 30),
                 })
            Assert.NotEqual("", SessionPolicy.Advice(info));
    }

    // ---- against a real server ------------------------------------------------------------------

    [SkippableFact]
    public async Task The_server_confirms_it_is_read_only_and_refuses_a_write()
    {
        // Asked of the server rather than of our own connection string (§4.7): the startup packet is the
        // whole mechanism, and a test that only re-reads what we built would pass with the packet ignored.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var info = PgTestServer.Info() with { ReadOnly = true };
        await using var factory = provider.CreateConnectionFactory(info, PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);

        var shown = await exec.ExecuteAsync(
            "show transaction_read_only", new QueryOptions(), CancellationToken.None);
        Assert.Equal("on", Assert.IsType<string>(shown[0].Rows[0][0]));

        // The point of doing this server-side: the refusal comes from Postgres, so it covers what a lexer
        // cannot see. A temp table is a write nobody would call destructive and it is still refused.
        var write = await exec.ExecuteAsync(
            "create temporary table bearing_read_only_probe (x int)", new QueryOptions(), CancellationToken.None);
        Assert.False(write[0].Success);
        Assert.Equal("25006", write[0].Error?.SqlState);
    }

    [SkippableFact]
    public async Task The_server_confirms_the_statement_timeout_and_enforces_it()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var info = PgTestServer.Info() with { StatementTimeoutSeconds = 1 };
        await using var factory = provider.CreateConnectionFactory(info, PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);

        // 1000 ms went out; "1s" comes back. That round trip is what says the GUC was set rather than the
        // string merely accepted.
        var shown = await exec.ExecuteAsync("show statement_timeout", new QueryOptions(), CancellationToken.None);
        Assert.Equal("1s", Assert.IsType<string>(shown[0].Rows[0][0]));

        // And it fires. CommandTimeout is 0 globally (§9.x), so nothing on the client side could produce
        // this: the cancellation is the server's.
        var slow = await exec.ExecuteAsync("select pg_sleep(5)", new QueryOptions(), CancellationToken.None);
        Assert.False(slow[0].Success);
        Assert.Equal("57014", slow[0].Error?.SqlState);
    }

    [SkippableFact]
    public async Task Every_connection_in_the_pool_is_read_only_not_just_the_first()
    {
        // The bug the startup packet exists to avoid. A SET issued once would have left the pool's other
        // sockets writable, and which one a statement got would decide whether it was refused. Ten
        // concurrent statements exceed the first connection, so more than one physical connection answers.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var info = PgTestServer.Info() with { ReadOnly = true };
        await using var factory = provider.CreateConnectionFactory(info, PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var probes = Enumerable.Range(0, 10).Select(_ => exec.ExecuteAsync(
            "show transaction_read_only", new QueryOptions(), CancellationToken.None)).ToList();

        foreach (var probe in await Task.WhenAll(probes))
            Assert.Equal("on", Assert.IsType<string>(probe[0].Rows[0][0]));
    }
}
