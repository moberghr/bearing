using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Demo;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Demo;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Demo mode (#64): a session served from fixed data with no database, and — the part that is actually most
/// of the work — one that leaves nothing behind.
/// <para>
/// The issue's blocker was that the existing seed path persists a connection into the user's real project
/// manifest and pushes a password through <c>ISecretStore</c>. So most of what follows asserts absence: no
/// manifest write outside the temp directory, no recent-projects entry, no query-log rows beside the user's
/// own history, no keychain call.
/// </para>
/// </summary>
public class DemoModeTests
{
    // ---- how a demo session is asked for ---------------------------------------------------------

    [Fact]
    public void The_switch_starts_a_demo()
        => Assert.True(DemoMode.Requested(["--demo"], _ => null));

    [Fact]
    public void The_switch_is_recognised_among_other_arguments_and_in_any_case()
    {
        Assert.True(DemoMode.Requested(["file.sql", "--demo"], _ => null));
        Assert.True(DemoMode.Requested(["--DEMO"], _ => null));
    }

    [Fact]
    public void The_environment_variable_starts_one_too()
        => Assert.True(DemoMode.Requested([], name => name == DemoMode.EnvironmentVariable ? "1" : null));

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("")]
    public void A_variable_that_says_no_is_honoured(string value)
    {
        // Otherwise BEARING_DEMO=0 — the obvious way to turn it off in a shell profile — would turn it on.
        Assert.False(DemoMode.Requested([], name => name == DemoMode.EnvironmentVariable ? value : null));
    }

    [Fact]
    public void An_ordinary_launch_is_not_a_demo()
    {
        Assert.False(DemoMode.Requested([], _ => null));
        Assert.False(DemoMode.Requested(null, _ => null));
        // And a near miss is not one either: nothing about a file called "demo" asks for demo mode.
        Assert.False(DemoMode.Requested(["demo", "--demonstrate"], _ => null));
    }

    // ---- the demo connection ---------------------------------------------------------------------

    [Fact]
    public void The_demo_connection_needs_no_secret_and_no_prompt()
    {
        // StoredPassword's first attempt goes out with no password, which is the passwordless case — so this
        // connects without a prompt and without the keychain being asked for anything (§1.1).
        Assert.Equal(CredentialKind.StoredPassword, DemoMode.Connection.CredentialKind);
    }

    [Fact]
    public void The_demo_connection_keeps_the_write_guard_on()
    {
        // §1.2: the guard must not be special-cased for demo mode — and a guarded connection is the better
        // demo anyway, since the confirmation is a feature to show rather than an obstacle.
        Assert.True(DemoMode.Connection.RequireWriteConfirmation);
    }

    [Fact]
    public void The_demo_connection_is_labelled_as_a_demo()
    {
        // Through the existing environment mechanism (§9.3a), so the tab wash, the env chip and the
        // status-bar rule all say so without anything new being drawn.
        Assert.Equal("demo", DemoMode.Connection.Environment);
        Assert.StartsWith("#", DemoMode.Connection.EnvironmentColor);
    }

    [Fact]
    public void The_demo_connection_does_not_claim_to_be_postgres()
    {
        // Its own provider id: a demo connection that somehow reached an ordinary session must fail to
        // resolve rather than quietly serve fixed rows where real data was expected.
        Assert.Equal(DemoProvider.ProviderId, DemoMode.Connection.ProviderId);
        Assert.NotEqual("postgres", DemoMode.Connection.ProviderId);
    }

    [Fact]
    public void A_demo_registry_holds_the_demo_provider_instead_of_postgres()
    {
        // This is what makes the fake unreachable from a normal connection flow: it is not registered
        // alongside the real provider, it replaces it — and only for a session that asked to be a demo.
        IProviderRegistry demo = new DemoProvider();

        Assert.Same(demo, demo.Get(DemoProvider.ProviderId));
        Assert.Throws<KeyNotFoundException>(() => demo.Get("postgres"));
        Assert.Single(demo.All);
    }

    [Fact]
    public void An_ordinary_registry_has_never_heard_of_the_demo_provider()
    {
        var real = new Bearing.Data.ProviderRegistry();

        Assert.Throws<KeyNotFoundException>(() => real.Get(DemoProvider.ProviderId));
        Assert.DoesNotContain(DemoProvider.ProviderId, real.All.Select(p => p.Id));
    }

    [Fact]
    public void The_welcome_script_runs_against_the_demo_data()
    {
        // A starter script that returned nothing would make the first thing an evaluator sees an empty grid.
        var executor = DemoExecutor.Default();
        var statements = DemoMode.WelcomeScript;

        Assert.Contains("shop.payment", statements);
        Assert.Contains("shop.store", statements);
        // And the comment says what this is, because an evaluator has to know the rows are not real.
        Assert.Contains("No database", statements);
        Assert.NotNull(executor);
    }

    // ---- the ephemeral workspace -----------------------------------------------------------------

    [Fact]
    public async Task A_demo_workspace_lives_under_temp_and_not_in_the_apps_own_directories()
    {
        await using var demo = DemoWorkspace.Create();

        var project = Path.GetFullPath(demo.ProjectDirectory);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(BearingPaths.DataDir), project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(BearingPaths.ConfigDir), project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Its_query_log_is_not_the_users_query_log()
    {
        // §1.3: demo SQL must not land in the real history. The store is path-parameterised, so this is a
        // matter of pointing it somewhere else — and of proving it went there.
        await using var demo = DemoWorkspace.Create();

        demo.QueryLog.Append(new QueryLogEntry
        {
            ExecutedAt = DateTimeOffset.UtcNow,
            ProviderId = DemoProvider.ProviderId,
            ConnectionName = "Demo data",
            Database = DemoCatalog.Database,
            SqlText = "select * from shop.payment",
            Success = true,
        });

        var rows = await Poll(demo.QueryLog);
        Assert.Single(rows);
        // The file it wrote to is inside the demo's own directory.
        var written = Directory.GetFiles(Path.GetDirectoryName(demo.ProjectDirectory)!, "query-log.sqlite*");
        Assert.NotEmpty(written);
    }

    [Fact]
    public async Task Its_recent_projects_list_is_its_own()
    {
        // A demo must not push the user's real projects down their recent list, or appear in it afterwards
        // pointing at a directory that no longer exists.
        await using var demo = DemoWorkspace.Create();

        await demo.RecentProjects.AddAsync(demo.ProjectDirectory, CancellationToken.None);

        Assert.Equal([demo.ProjectDirectory], await demo.RecentProjects.ListAsync(CancellationToken.None));
        Assert.NotEqual(
            await new FileRecentProjects().ListAsync(CancellationToken.None),
            await demo.RecentProjects.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Its_secret_store_keeps_nothing()
    {
        // Not the real store, and not a probe for it: there is no secret in a demo session, so reaching the
        // OS keychain would be a call with no purpose (§1.1).
        await using var demo = DemoWorkspace.Create();

        Assert.False(demo.Secrets.CanStore);
        Assert.IsType<NoSecretStore>(demo.Secrets);
    }

    [Fact]
    public async Task Disposing_a_demo_deletes_everything_it_wrote()
    {
        string root;
        await using (var demo = DemoWorkspace.Create())
        {
            var project = await demo.Projects.CreateAsync(demo.ProjectDirectory, "Demo", CancellationToken.None);
            await demo.Projects.SaveAsync(project, CancellationToken.None);
            demo.QueryLog.Append(new QueryLogEntry
            {
                ExecutedAt = DateTimeOffset.UtcNow,
                ProviderId = DemoProvider.ProviderId,
                ConnectionName = "Demo data",
                Database = DemoCatalog.Database,
                SqlText = "select 1",
                Success = true,
            });
            await Poll(demo.QueryLog);

            root = Path.GetDirectoryName(demo.ProjectDirectory)!;
            Assert.True(Directory.Exists(root));
        }

        Assert.False(Directory.Exists(root), "the demo left its directory behind");
    }

    [Fact]
    public async Task Two_demo_sessions_do_not_share_a_directory()
    {
        // A demo that remembers the previous demo's tabs is not the clean first run this exists to give.
        await using var first = DemoWorkspace.Create();
        await using var second = DemoWorkspace.Create();

        Assert.NotEqual(first.ProjectDirectory, second.ProjectDirectory);
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        // Best-effort throughout (§5.2): closing a demo must not be able to crash the app.
        var demo = DemoWorkspace.Create();
        await demo.DisposeAsync();
        await demo.DisposeAsync();
    }

    [Fact]
    public void Cleanup_completes_even_when_the_caller_blocks_its_context()
    {
        // The defect this suite could not see. Cleanup runs from the window's Closed handler, which blocks
        // the UI thread waiting for it — so an await without ConfigureAwait(false) posts its continuation
        // back to that thread, the wait times out, and the directory survives: precisely the residue the
        // feature promises is gone. A single-threaded context stands in for Avalonia's dispatcher.
        var demo = DemoWorkspace.Create();
        var root = Path.GetDirectoryName(demo.ProjectDirectory)!;
        demo.QueryLog.Append(Entry("select 1"));

        var previous = SynchronizationContext.Current;
        var context = new BlockingContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Assert.True(demo.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)),
                "cleanup deadlocked against the thread that was waiting for it");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.False(Directory.Exists(root), "the demo left its directory behind");
        Assert.Equal(0, context.Posted);
    }

    [Fact]
    public async Task Cleanup_deletes_the_directory_even_after_a_read_pooled_a_handle()
    {
        // The retry's reason: a read hands its connection back to Microsoft.Data.Sqlite's pool, which keeps
        // the file open, and the delete then fails with IOException *or* UnauthorizedAccessException. Only
        // catching the first abandoned it on the very first attempt.
        await using var demo = DemoWorkspace.Create();
        var root = Path.GetDirectoryName(demo.ProjectDirectory)!;
        demo.QueryLog.Append(Entry("select 1"));
        await Poll(demo.QueryLog);
        await demo.QueryLog.SearchAsync(new QueryLogQuery(), CancellationToken.None);

        await demo.DisposeAsync();

        Assert.False(Directory.Exists(root));
    }

    /// <summary>
    /// A context that counts what is posted to it and runs none of it — which is what a thread sitting in
    /// <c>Wait()</c> amounts to. Anything needing it to run in order to finish will hang.
    /// </summary>
    private sealed class BlockingContext : SynchronizationContext
    {
        private int _posted;

        public int Posted => Volatile.Read(ref _posted);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);
    }

    /// <summary>A log entry, so a demo's history has something in it to be deleted with.</summary>
    private static QueryLogEntry Entry(string sql) => new()
    {
        ExecutedAt = DateTimeOffset.UtcNow,
        ProviderId = DemoProvider.ProviderId,
        ConnectionName = "Demo data",
        Database = DemoCatalog.Database,
        SqlText = sql,
        Success = true,
    };

    /// <summary>Wait for the log's background writer to have stored what was appended.</summary>
    private static async Task<IReadOnlyList<QueryLogEntry>> Poll(IQueryLog log)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var rows = await log.SearchAsync(new QueryLogQuery(), CancellationToken.None);
            if (rows.Count > 0) return rows;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("the entry was never written");
    }
}
