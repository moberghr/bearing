using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The server's own sessions, and the two actions on one (#101).
/// <para>
/// Almost everything here can only be established against a live server. `pg_stat_activity` is the server
/// describing itself, and both actions are functions whose effect is on another connection — a fixture that
/// asserted our SQL string back would pass over a query that never worked.
/// </para>
/// </summary>
public class ServerActivityTests
{
    private static IDbProvider Provider() => new ProviderRegistry().Get(PostgresProvider.ProviderId);

    // ---- reading -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Our_own_backend_is_listed_and_the_reader_is_not()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var activity = provider.CreateServerActivity(factory);
        var executor = provider.CreateQueryExecutor(factory);

        // Hold a second backend open so there is something of ours to find that is not the reader itself.
        using var busy = new CancellationTokenSource();
        var running = executor.ExecuteAsync("select pg_sleep(20)", new QueryOptions(), busy.Token);
        try
        {
            var ours = await WaitForOursAsync(activity);

            Assert.NotNull(ours);
            Assert.Equal(PostgresServerActivity.OurApplicationName, ours!.Application);
            Assert.True(ours.IsOurs);
            Assert.Equal(PgTestServer.Database, ours.Database);

            // The connection doing the reading is excluded: it is the one row the user can never act on, and
            // a panel that polls itself reports its own poll.
            var read = await activity.GetActivityAsync(ActivityFilter.Everything, CancellationToken.None);
            var self = await CurrentPidAsync(executor);
            Assert.DoesNotContain(read.Backends, b => b.Pid == self);
        }
        finally
        {
            busy.Cancel();
            await Swallow(running);
        }
    }

    [SkippableFact]
    public async Task A_running_statement_reports_how_long_the_server_says_it_has_been_running()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var activity = provider.CreateServerActivity(factory);
        var executor = provider.CreateQueryExecutor(factory);

        using var busy = new CancellationTokenSource();
        var running = executor.ExecuteAsync("select pg_sleep(20)", new QueryOptions(), busy.Token);
        try
        {
            var ours = await WaitForOursAsync(activity, b => b.State == "active" && b.RunningFor is not null);

            Assert.NotNull(ours);
            // A duration the server computed, not a timestamp this machine subtracted from — which is the
            // whole reason the record carries a TimeSpan.
            Assert.True(ours!.RunningFor > TimeSpan.Zero, $"running_for was {ours.RunningFor}");
            Assert.True(ours.RunningFor < TimeSpan.FromMinutes(5), "a just-started statement reported minutes");
        }
        finally
        {
            busy.Cancel();
            await Swallow(running);
        }
    }

    // ---- acting --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Cancel_stops_the_statement_and_leaves_the_session()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var activity = provider.CreateServerActivity(factory);

        // A second factory, so the cancelled backend is unambiguously not one the reader is using.
        await using var victimFactory = provider.CreateConnectionFactory(
            PgTestServer.Info("activity-victim"), PgTestServer.Password);
        var victim = provider.CreateQueryExecutor(victimFactory);

        var running = victim.ExecuteAsync("select pg_sleep(30)", new QueryOptions(), CancellationToken.None);
        var pid = await WaitForPidAsync(activity, "pg_sleep(30)");
        Skip.If(pid is null, "the sleeping backend never appeared in pg_stat_activity");

        Assert.True(await activity.CancelBackendAsync(pid!.Value, CancellationToken.None));

        // The proof that the function was called rather than the SQL merely accepted: the *other* connection's
        // statement comes back cancelled.
        var results = await running;
        Assert.False(results[0].Success);
        Assert.Equal("57014", results[0].Error?.SqlState);

        // Cancelling is not disconnecting — the session is still there to run the next statement.
        var after = await victim.ExecuteAsync("select 1", new QueryOptions(), CancellationToken.None);
        Assert.True(after[0].Success);
    }

    [SkippableFact]
    public async Task Terminate_ends_the_session_and_the_backend_is_gone_from_the_next_read()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var activity = provider.CreateServerActivity(factory);

        await using var victimFactory = provider.CreateConnectionFactory(
            PgTestServer.Info("activity-victim"), PgTestServer.Password);
        var victim = provider.CreateQueryExecutor(victimFactory);

        var running = victim.ExecuteAsync("select pg_sleep(30)", new QueryOptions(), CancellationToken.None);
        var pid = await WaitForPidAsync(activity, "pg_sleep(30)");
        Skip.If(pid is null, "the sleeping backend never appeared in pg_stat_activity");

        Assert.True(await activity.TerminateBackendAsync(pid!.Value, CancellationToken.None));
        await Swallow(running);

        var gone = await WaitUntilAsync(async () =>
            !(await activity.GetActivityAsync(ActivityFilter.Everything, CancellationToken.None)).Backends.Any(b => b.Pid == pid));
        Assert.True(gone, "the terminated backend was still listed");
    }

    [SkippableFact]
    public async Task Asking_about_a_backend_that_is_already_gone_is_answered_no_rather_than_thrown()
    {
        // The reason the two actions return the server's boolean instead of discarding it: a pid from a poll
        // two seconds ago may have finished on its own, and that is an ordinary outcome, not a failure.
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var activity = provider.CreateServerActivity(factory);

        Assert.False(await activity.CancelBackendAsync(int.MaxValue, CancellationToken.None));
        Assert.False(await activity.TerminateBackendAsync(int.MaxValue, CancellationToken.None));
    }

    // ---- what a role may see -------------------------------------------------------------------

    [SkippableFact]
    public async Task A_role_without_pg_read_all_stats_sees_only_itself_and_says_so()
    {
        // The finding this whole design turns on, and the reason ServerActivity carries a flag rather than a
        // count of hidden rows: a restricted role does NOT get other sessions with their details blanked out.
        // It does not get the rows at all, so nothing in the result set can reveal the absence.
        var provider = Provider();
        await using var admin = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireWritableAsync(admin);

        var adminExec = provider.CreateQueryExecutor(admin);
        const string role = "bearing_activity_probe";
        const string password = "probe";

        await adminExec.ExecuteAsync($"drop role if exists {role}", new QueryOptions(), CancellationToken.None);
        await adminExec.ExecuteAsync(
            $"create role {role} login password '{password}'", new QueryOptions(), CancellationToken.None);
        try
        {
            var restricted = PgTestServer.Info("activity-probe") with { User = role };
            await using var probeFactory = provider.CreateConnectionFactory(restricted, password);
            var probe = provider.CreateServerActivity(probeFactory);

            // Something for it not to see.
            using var busy = new CancellationTokenSource();
            var running = adminExec.ExecuteAsync("select pg_sleep(20)", new QueryOptions(), busy.Token);
            try
            {
                var seen = await probe.GetActivityAsync(ActivityFilter.Everything, CancellationToken.None);

                Assert.False(seen.SeesAllSessions);
                Assert.DoesNotContain(seen.Backends, b => b.User == PgTestServer.User);

                // And the admin, at the same moment, does see it — otherwise the assertion above would pass
                // on a quiet server and prove nothing.
                var adminActivity = provider.CreateServerActivity(admin);
                var all = await adminActivity.GetActivityAsync(ActivityFilter.Everything, CancellationToken.None);
                Assert.True(all.SeesAllSessions);
                Assert.Contains(all.Backends, b => b.User == PgTestServer.User);
            }
            finally
            {
                busy.Cancel();
                await Swallow(running);
            }
        }
        finally
        {
            // One statement per call: Npgsql runs a batch in one implicit transaction, so a failing drop would
            // abort anything behind it and leave a role on a shared server (§4.7).
            await Swallow(adminExec.ExecuteAsync(
                $"drop role if exists {role}", new QueryOptions(), CancellationToken.None));
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static async Task<BackendActivity?> WaitForOursAsync(
        IServerActivity activity, Func<BackendActivity, bool>? extra = null)
    {
        BackendActivity? found = null;
        await WaitUntilAsync(async () =>
        {
            var read = await activity.GetActivityAsync(ActivityFilter.Everything, CancellationToken.None);
            found = read.Backends.FirstOrDefault(b => b.IsOurs && (extra is null || extra(b)));
            return found is not null;
        });
        return found;
    }

    private static async Task<int?> WaitForPidAsync(IServerActivity activity, string queryFragment)
    {
        int? pid = null;
        await WaitUntilAsync(async () =>
        {
            var read = await activity.GetActivityAsync(ActivityFilter.Everything, CancellationToken.None);
            pid = read.Backends
                .FirstOrDefault(b => b.Query?.Contains(queryFragment, StringComparison.Ordinal) == true)?.Pid;
            return pid is not null;
        });
        return pid;
    }

    /// <summary>Poll a condition for a few seconds. `pg_stat_activity` is a snapshot of a moving server, so a
    /// single read straight after starting a statement is a race — losing it would look like a query bug.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<int> CurrentPidAsync(IQueryExecutor executor)
    {
        var results = await executor.ExecuteAsync(
            "select pg_backend_pid()", new QueryOptions(), CancellationToken.None);
        return Convert.ToInt32(results[0].Rows[0][0]);
    }

    private static async Task Swallow(Task task)
    {
        try { await task; } catch { /* the point of these is that they end, not how */ }
    }

    private static async Task Swallow<T>(Task<T> task)
    {
        try { await task; } catch { /* as above */ }
    }
}
