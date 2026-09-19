using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The ways an open transaction can be lost, mis-reported or left behind that a code review of #131 found —
/// each one a path where uncommitted work disappears without the user agreeing to it, which is the single
/// failure manual-commit mode exists to prevent.
/// <para>
/// Kept apart from <see cref="ManualCommitTests"/> (the happy paths and the guards as designed) because these
/// are regression tests with a shared shape: <i>the user said no, or said nothing, and the work survived</i>.
/// </para>
/// </summary>
public class ManualCommitGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-txg", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private sealed record Harness(WorkspaceContext Ctx, ExecutionViewModel Exec, ConnectionInfo Conn);

    private Harness NewHarness(IDialogService? dialogs = null, AppSettings? settings = null,
        IQueryExecutor? executor = null)
    {
        Directory.CreateDirectory(_root);
        var ctx = new WorkspaceContext(
            new FakeProvider { Executor = executor },
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(settings ?? new AppSettings { AutosaveMode = AutosaveMode.Off }));
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "prod",
            ProviderId = "postgres",
            Database = "app",
            ManualCommit = true,
        };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        return new Harness(ctx, new ExecutionViewModel(ctx, dialogs), conn);
    }

    private static EditorTabViewModel Tab(Harness h, string name = "t")
    {
        var tab = new EditorTabViewModel(name) { ConnectionId = h.Conn.Id };
        h.Ctx.Tabs.Add(tab);
        h.Ctx.SelectedTab = tab;
        return tab;
    }

    private static FakeTransactionScope ScopeOf(Harness h, EditorTabViewModel tab)
        => Assert.IsType<FakeTransactionScope>(h.Ctx.Transactions.For(tab)!.Scope);

    private static WorkspaceViewModel Workspace(Harness h, IDialogService? dialogs)
        => new(h.Ctx, new ScriptsViewModel(h.Ctx, () => { }), new ConnectionsViewModel(h.Ctx, dialogs), dialogs);

    // ---- a statement in flight owns the connection ------------------------------------------------

    /// <summary>
    /// The transaction and the statement running in it share one physical connection, so a commit issued
    /// mid-statement throws from the driver — and the teardown behind it would dispose that connection under
    /// a live reader, ending the transaction on the server with the app believing it had committed.
    /// </summary>
    [Fact]
    public async Task Commit_and_rollback_are_refused_while_the_tab_is_running()
    {
        var gated = new GatedExecutor();
        var h = NewHarness(executor: gated);
        var tab = Tab(h);
        var scope = await OpenThrough(h, tab, gated);

        var running = h.Exec.ExecuteAsync("insert into t values (2)");
        await WaitUntil(() => tab.IsRunning);

        Assert.False(h.Exec.CanCommit);
        Assert.False(h.Exec.CanRollback);

        var status = "";
        h.Ctx.Status = s => status = s;
        await h.Exec.CommitTransactionAsync();
        Assert.Contains("cancel it with Esc", status);
        Assert.Equal(0, scope.Commits);

        await h.Exec.RollbackTransactionAsync();
        Assert.Equal(0, scope.Rollbacks);
        Assert.NotNull(h.Ctx.Transactions.For(tab));

        gated.Release("values (2)");
        await running;
        Assert.True(h.Exec.CanCommit);   // available again the moment the statement is done
    }

    /// <summary>
    /// The auto-rollback is the same hazard without a user behind it. A long statement used to age past the
    /// threshold <i>while running</i>, because the idle clock was stamped when it started — so the sweep
    /// rolled back a transaction with a live command on its connection, unattended.
    /// </summary>
    [Fact]
    public async Task The_sweep_leaves_a_running_tab_alone_however_old_its_clock_reads()
    {
        var gated = new GatedExecutor();
        var h = NewHarness(
            settings: new AppSettings
            {
                AutosaveMode = AutosaveMode.Off,
                IdleTransactionWarnMinutes = 5,
                IdleTransactionRollbackMinutes = 15,
            },
            executor: gated);
        var tab = Tab(h);
        var scope = await OpenThrough(h, tab, gated);

        var running = h.Exec.ExecuteAsync("insert into t values (2)");
        await WaitUntil(() => tab.IsRunning);
        scope.Idle(TimeSpan.FromHours(1));

        await h.Exec.SweepTransactionsAsync();

        Assert.Equal(0, scope.Rollbacks);
        Assert.NotNull(h.Ctx.Transactions.For(tab));

        gated.Release("values (2)");
        await running;
    }

    /// <summary>
    /// A forced rollback cannot refuse the way the two verbs do — the disconnect is already happening — so
    /// it cancels the statement first. Without that it disposed the pinned connection under a live reader,
    /// which is the very thing EndTransactionAsync refuses by hand.
    /// </summary>
    [Fact]
    public async Task A_forced_rollback_cancels_the_statement_before_tearing_the_connection_down()
    {
        var dialogs = new FakeDialogs();
        var gated = new GatedExecutor();
        var h = NewHarness(dialogs: dialogs, executor: gated);
        var tab = Tab(h);
        var scope = await OpenThrough(h, tab, gated);

        var running = h.Exec.ExecuteAsync("insert into t values (2)");
        await WaitUntil(() => tab.IsRunning);

        await TransactionGuard.ConfirmAsync(h.Ctx.Transactions, dialogs, _ => true, "disconnect");

        Assert.Equal(1, scope.Rollbacks);
        Assert.Null(h.Ctx.Transactions.For(tab));
        // The run was cancelled rather than left to die with the connection.
        await running;
        Assert.False(tab.IsRunning);
    }

    // ---- asking is not acting ----------------------------------------------------------------------

    /// <summary>
    /// Quit asks two questions. Answering the first ("yes, roll them back") and then declining the second
    /// ("no, keep my queries running") cancels the quit — so the transactions must still be there. Rolling
    /// back between the two questions lost them for an action that never happened.
    /// </summary>
    [Fact]
    public async Task Declining_the_running_query_prompt_leaves_the_transactions_intact()
    {
        var dialogs = new FakeDialogs { DiscardTransactionsAnswer = true, CancelRunningAnswer = false };
        var gated = new GatedExecutor();
        // The real shell, because QuitGuard reads the transactions through it and there is no other route.
        var vm = await NewShellAsync(dialogs, gated);
        var tab = vm.Workspace.Tabs[0];

        var run1 = vm.Execution.ExecuteAsync("insert into t values (1)");
        await WaitUntil(() => gated.Started >= 1);
        gated.Release("values (1)");
        await run1;
        var scope = Assert.IsType<FakeTransactionScope>(vm.Context.Transactions.For(tab)!.Scope);

        var running = vm.Execution.ExecuteAsync("insert into t values (2)");
        await WaitUntil(() => tab.IsRunning);

        Assert.False(await QuitGuard.ConfirmAsync(vm, dialogs, running: 1));

        // It asked about both, and acted on neither — the quit was cancelled.
        Assert.Single(dialogs.TransactionPrompts);
        Assert.Equal(0, scope.Rollbacks);
        Assert.NotNull(vm.Context.Transactions.For(tab));

        gated.Release("values (2)");
        await running;
    }

    /// <summary>
    /// A close asks about the transaction before the unsaved-text prompt — so that nobody decides what to
    /// save on a tab they then keep — but cancelling at that later prompt keeps the tab. The transaction has
    /// to still be there: unlike a cancelled query, it cannot be re-run.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_unsaved_text_prompt_keeps_the_transaction_too()
    {
        var dialogs = new FakeDialogs(CloseChoice.Cancel);
        var h = NewHarness(dialogs: dialogs);
        var tab = Tab(h);
        tab.Text = "select 1";   // unsaved work, so the second prompt is raised
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        Assert.False(await Workspace(h, dialogs).CloseTabAsync(tab));

        Assert.Single(dialogs.TransactionPrompts);        // it did ask
        Assert.Equal(0, scope.Rollbacks);                 // and then the close was abandoned
        Assert.NotNull(h.Ctx.Transactions.For(tab));
        Assert.Contains(tab, h.Ctx.Tabs);
    }

    // ---- paths that skip the guard entirely ---------------------------------------------------------

    /// <summary>
    /// Deleting the script removes its tabs without going through <c>CloseTabAsync</c>. A transaction left
    /// behind there is unreachable — no tab can select it, so neither the chip nor either command can ever
    /// reach it — while it goes on holding a pooled connection and a lease that stops the session being
    /// disposed.
    /// </summary>
    [Fact]
    public async Task Deleting_a_script_ends_the_transaction_of_the_tabs_it_closes()
    {
        var h = NewHarness();
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        var path = Path.Combine(_root, "scripts", "doomed.sql");
        File.WriteAllText(path, "select 1");

        var tab = Tab(h, "doomed.sql");
        tab.ScriptPath = path;
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        Assert.True(Workspace(h, null).DeleteScript(path));
        await WaitUntil(() => h.Ctx.Transactions.Count == 0);

        Assert.Equal(1, scope.Rollbacks);
    }

    /// <summary>
    /// Un-ticking Manual commit does not change the pool — it reaches no server — so nothing else on the
    /// save path would have ended the transaction. Left open it would keep taking this tab's writes
    /// uncommitted, while the refusal that stops a typed <c>COMMIT</c> reaching it disappeared with the
    /// setting.
    /// </summary>
    [Fact]
    public async Task Turning_manual_commit_off_ends_the_open_transaction()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        var status = "";
        h.Ctx.Status = s => status = s;
        var connections = new ConnectionsViewModel(h.Ctx);
        await connections.AddOrUpdateConnectionAsync(h.Conn with { ManualCommit = false }, password: null);

        Assert.Equal(1, scope.Rollbacks);
        Assert.Null(h.Ctx.Transactions.For(tab));
    }

    /// <summary>Re-selecting the connection a tab is already on changes nothing, so it must not be reported
    /// as a switch the transaction is in the way of. Double-clicking the tree's server row does exactly
    /// this.</summary>
    [Fact]
    public async Task Re_selecting_the_same_connection_is_not_refused()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");

        var status = "";
        h.Ctx.Status = s => status = s;
        new ConnectionsViewModel(h.Ctx).SetTabConnection(tab, h.Conn.Id);

        Assert.Equal("", status);
        Assert.Equal(h.Conn.Id, tab.ConnectionId);
    }

    /// <summary>
    /// EXPLAIN ANALYZE runs the statement, and it carries its own <c>BEGIN … ROLLBACK</c>. On the held
    /// connection that rollback would end the user's transaction; on a pooled one it blocks on the rows this
    /// tab's own transaction has locked — and while it blocks the tab counts as running, so Commit and
    /// Rollback are both out of reach. Refused instead, with the plan-only form named as the way through.
    /// </summary>
    [Fact]
    public async Task Explain_analyze_is_refused_while_this_tab_holds_a_transaction()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");

        var status = "";
        h.Ctx.Status = s => status = s;
        Assert.Null(await h.Exec.ExplainAsync("select 1", analyze: true));

        Assert.Contains("cannot join it", status);
        Assert.Contains("prod/app", status);
        Assert.NotNull(h.Ctx.Transactions.For(tab));
    }

    /// <summary>The plan-only form executes nothing and carries no transaction control, so it is not
    /// refused — and it runs on the transaction, where the plan describes what the user is looking at.</summary>
    [Fact]
    public async Task A_plan_only_explain_still_runs_and_joins_the_transaction()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);
        var before = scope.StatementCount;

        await h.Exec.ExplainAsync("select 1", analyze: false);

        Assert.Equal(before + 1, scope.StatementCount);   // it ran, and it ran in there
    }

    /// <summary>
    /// A project removed takes its tabs with it — the ones on screen and the ones parked — so a transaction
    /// on one becomes unreachable rather than merely off screen: nothing can select it, so neither the chip
    /// nor either command can ever reach it, while it holds a pooled connection and a lease.
    /// </summary>
    [Fact]
    public async Task Removing_a_project_ends_the_transactions_of_the_tabs_it_drops()
    {
        var dialogs = new FakeDialogs();
        var gated = new GatedExecutor();
        var vm = await NewShellAsync(dialogs, gated);
        var tab = vm.Workspace.Tabs[0];

        var opening = vm.Execution.ExecuteAsync("insert into t values (1)");
        await WaitUntil(() => gated.Started >= 1);
        gated.Release("values (1)");
        await opening;
        var scope = Assert.IsType<FakeTransactionScope>(vm.Context.Transactions.For(tab)!.Scope);

        var home = vm.ProjectDirectory!;

        // A second project to land in — removal refuses when there is nowhere to go. Switching there parks
        // the first project's tabs rather than closing them, so the transaction survives and comes back.
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        await vm.OpenProjectAsync(other);
        Assert.NotNull(vm.Context.Transactions.For(tab));
        Assert.Equal(0, scope.Rollbacks);

        await vm.OpenProjectAsync(home);                    // back, and the tab is the same instance
        Assert.Contains(tab, vm.Workspace.Tabs);

        // Removing it is what actually drops the tabs.
        Assert.True(await vm.RemoveCurrentProjectAsync(deleteFromDisk: false));

        Assert.Equal(1, scope.Rollbacks);
        Assert.Equal(0, vm.Context.Transactions.Count);
    }

    // ---- what the chip and the log say ---------------------------------------------------------------

    /// <summary>
    /// The chip counts statements the user wrote. A row count run for a write confirmation (§1.5) or a press
    /// of <c>[Count]</c> is a probe Bearing makes on their behalf — counting those made the chip claim
    /// uncommitted work that nobody had written.
    /// </summary>
    [Fact]
    public async Task A_count_probe_does_not_inflate_the_uncommitted_count()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);
        Assert.Equal(1, scope.StatementCount);

        await scope.Executor.CountAsync("select count(*) from (select 1) _sq", CancellationToken.None);

        Assert.Equal(1, scope.StatementCount);
        Assert.Contains("1 uncommitted", h.Exec.TransactionText);
    }

    /// <summary>
    /// §1.6's contract is that the commit or rollback that ended a transaction is in the log beside the
    /// statements that ran inside it. That has to hold for the rollbacks the guards force too — a history
    /// that recorded only the two buttons could not tell "committed" from "discarded on the way out".
    /// </summary>
    [Fact]
    public async Task A_rollback_forced_by_a_guard_is_logged_like_any_other()
    {
        var dialogs = new FakeDialogs();
        var h = NewHarness(dialogs: dialogs);
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var id = h.Ctx.Transactions.For(tab)!.Id;

        Assert.True(await Workspace(h, dialogs).CloseTabAsync(tab));

        var rows = await Eventually(h.Ctx.QueryLog, 2);
        Assert.Equal(["rollback", "insert into t values (1)"], rows.Select(r => r.SqlText));
        Assert.All(rows, r => Assert.Equal(id, r.TransactionId));
    }

    /// <summary>
    /// A commit that threw still <b>ended</b> the transaction — the scope releases its connection either
    /// way — so the log needs its terminating row. Without one the statements carry a transaction id nothing
    /// matches, which reads exactly like a transaction still open (§1.6). Recorded as a failure, because
    /// "tried to commit and could not" is a third outcome and not either of the other two.
    /// </summary>
    [Fact]
    public async Task A_commit_that_fails_is_still_logged_as_the_end_of_the_transaction()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var open = h.Ctx.Transactions.For(tab)!;
        ScopeOf(h, tab).FailToEnd = "the server went away";

        var status = "";
        h.Ctx.Status = s => status = s;
        await h.Exec.CommitTransactionAsync();

        Assert.Contains("Commit failed", status);
        Assert.Null(h.Ctx.Transactions.For(tab));   // it is over regardless

        var rows = await Eventually(h.Ctx.QueryLog, 2);
        var end = rows[0];
        Assert.Equal("commit", end.SqlText);
        Assert.Equal(open.Id, end.TransactionId);
        Assert.False(end.Success);
        Assert.Equal("the server went away", end.ErrorMessage);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>
    /// A real shell over the fake provider, with one manual-commit connection and the scratch tab pointed at
    /// it — the quit guard reads its transactions through <c>ShellViewModel.Context</c> and its tabs through
    /// <c>Workspace.AllTabs</c>, so nothing smaller will do.
    /// </summary>
    private async Task<ShellViewModel> NewShellAsync(
        IDialogService dialogs, IQueryExecutor executor,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        Directory.CreateDirectory(_root);
        var vm = new ShellViewModel(
            new FakeProvider { Executor = executor },
            new JsonProjectStore(), new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            dialogs: dialogs,
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        await vm.InitializeAsync(Path.Combine(_root, name));

        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "prod",
            ProviderId = "postgres",
            Database = "app",
            ManualCommit = true,
        };
        await vm.Connections.AddOrUpdateConnectionAsync(conn, password: null);
        vm.Workspace.Tabs[0].ConnectionId = conn.Id;
        vm.Context.SelectedTab = vm.Workspace.Tabs[0];
        return vm;
    }

    /// <summary>Open the tab's transaction through a gated executor: the statement that opens it has to be
    /// let through before a second can be held in flight.</summary>
    private static async Task<FakeTransactionScope> OpenThrough(
        Harness h, EditorTabViewModel tab, GatedExecutor gated)
    {
        var opening = h.Exec.ExecuteAsync("insert into t values (1)");
        await WaitUntil(() => gated.Started >= 1);
        gated.Release("values (1)");
        await opening;
        return ScopeOf(h, tab);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "condition did not hold within the timeout");
            await Task.Delay(10);
        }
    }

    private static async Task<IReadOnlyList<QueryLogEntry>> Eventually(IQueryLog log, int count)
    {
        for (var i = 0; i < 100; i++)
        {
            var rows = await log.SearchAsync(new QueryLogQuery { Limit = null }, CancellationToken.None);
            if (rows.Count >= count) return rows;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"the log never reached {count} rows");
    }
}
