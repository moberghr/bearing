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
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Manual-commit mode's bookkeeping (#131): when a transaction opens, what joins it, what ends it, and every
/// path that would otherwise destroy one without saying so.
/// <para>
/// Deliberately <b>not</b> what a transaction does on a server — nothing here proves a rollback undoes
/// anything, because the scope under these tests has no server (§4.6: a fixture asserting our own
/// assumptions back at us is a green suite over a broken app). That half is
/// <c>ManualCommitLiveTests</c>, against both engines.
/// </para>
/// </summary>
public class ManualCommitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-tx", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private sealed record Harness(
        WorkspaceContext Ctx, ExecutionViewModel Exec, FakeProvider Provider, ConnectionInfo Conn);

    private Harness NewHarness(
        bool manualCommit = true, IDialogService? dialogs = null, AppSettings? settings = null,
        FakeExecutor? executor = null)
    {
        Directory.CreateDirectory(_root);
        var provider = new FakeProvider { Executor = executor };
        var ctx = new WorkspaceContext(
            provider,
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
            ManualCommit = manualCommit,
        };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        return new Harness(ctx, new ExecutionViewModel(ctx, dialogs), provider, conn);
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

    // ---- when one opens -------------------------------------------------------------------------

    /// <summary>
    /// The fork the design turns on: reading opens nothing. A transaction left open by nothing but browsing
    /// is the commonest way to end up holding locks on a production server, so a SELECT still auto-commits.
    /// </summary>
    [Fact]
    public async Task A_read_opens_no_transaction()
    {
        var h = NewHarness();
        var tab = Tab(h);

        await h.Exec.ExecuteAsync("select 1");

        Assert.Null(h.Ctx.Transactions.For(tab));
        Assert.False(h.Exec.HasOpenTransaction);
    }

    [Fact]
    public async Task A_write_opens_one_and_the_chip_reports_it()
    {
        var h = NewHarness();
        var tab = Tab(h);

        await h.Exec.ExecuteAsync("insert into t values (1)");

        Assert.NotNull(h.Ctx.Transactions.For(tab));
        Assert.True(h.Exec.HasOpenTransaction);
        Assert.True(h.Exec.CanCommit);
        Assert.Contains("uncommitted", h.Exec.TransactionText);
        Assert.Contains("prod/app", h.Exec.TransactionTip);
    }

    /// <summary>An ordinary connection is untouched by any of this — the setting is per connection, and
    /// this is what says the feature costs nothing to everyone who has not turned it on.</summary>
    [Fact]
    public async Task An_auto_commit_connection_opens_nothing_even_for_a_write()
    {
        var h = NewHarness(manualCommit: false);
        var tab = Tab(h);

        await h.Exec.ExecuteAsync("insert into t values (1)");

        Assert.Null(h.Ctx.Transactions.For(tab));
    }

    /// <summary>
    /// Once one is open, everything the tab runs joins it — reads included. That is what makes the grid and
    /// the row-count preview agree with what the transaction has actually done, and it is the reason the
    /// rule is "the first write opens one", not "writes run in one".
    /// </summary>
    [Fact]
    public async Task Once_open_a_read_joins_it_rather_than_opening_a_second()
    {
        var h = NewHarness();
        var tab = Tab(h);

        await h.Exec.ExecuteAsync("insert into t values (1)");
        var opened = h.Ctx.Transactions.For(tab);
        await h.Exec.ExecuteAsync("select 1");

        Assert.Same(opened, h.Ctx.Transactions.For(tab));
        Assert.Equal(1, h.Ctx.Transactions.Count);
        Assert.Equal(2, ScopeOf(h, tab).StatementCount);
    }

    /// <summary>Per tab, not per connection: two tabs on the same pool get one each, or a Commit in one
    /// would commit work the other never showed anybody.</summary>
    [Fact]
    public async Task Two_tabs_on_one_connection_get_a_transaction_each()
    {
        var h = NewHarness();
        var a = Tab(h, "a");
        await h.Exec.ExecuteAsync("insert into t values (1)");

        var b = Tab(h, "b");
        await h.Exec.ExecuteAsync("insert into t values (2)");

        Assert.Equal(2, h.Ctx.Transactions.Count);
        Assert.NotSame(h.Ctx.Transactions.For(a), h.Ctx.Transactions.For(b));
    }

    /// <summary>
    /// A held transaction is a pooled connection checked out indefinitely, and a pool holds ten. Past the
    /// cap the refusal is a sentence rather than every other operation on that database quietly blocking.
    /// </summary>
    [Fact]
    public async Task The_pool_cap_refuses_one_transaction_too_many_and_says_why()
    {
        var h = NewHarness();
        var status = "";
        h.Ctx.Status = s => status = s;

        for (var i = 0; i < CommitPolicy.MaxOpenPerPool; i++)
        {
            Tab(h, $"t{i}");
            await h.Exec.ExecuteAsync("insert into t values (1)");
        }
        Assert.Equal(CommitPolicy.MaxOpenPerPool, h.Ctx.Transactions.Count);

        var extra = Tab(h, "one-too-many");
        await h.Exec.ExecuteAsync("insert into t values (1)");

        Assert.Null(h.Ctx.Transactions.For(extra));
        Assert.Equal(CommitPolicy.MaxOpenPerPool, h.Ctx.Transactions.Count);
        Assert.Contains("prod/app", status);
        Assert.Contains("Commit or roll one back", status);
    }

    // ---- ending one -----------------------------------------------------------------------------

    [Fact]
    public async Task Commit_ends_it_and_says_what_was_committed()
    {
        var h = NewHarness();
        var tab = Tab(h);
        var status = "";
        h.Ctx.Status = s => status = s;

        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);
        await h.Exec.CommitTransactionAsync();

        Assert.Equal(1, scope.Commits);
        Assert.Null(h.Ctx.Transactions.For(tab));
        Assert.False(h.Exec.HasOpenTransaction);
        Assert.Contains("Committed 1 statement on prod/app", status);
    }

    [Fact]
    public async Task Rollback_ends_it_too()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        await h.Exec.RollbackTransactionAsync();

        Assert.Equal(1, scope.Rollbacks);
        Assert.Equal(0, scope.Commits);
        Assert.Null(h.Ctx.Transactions.For(tab));
    }

    /// <summary>
    /// A statement that failed leaves the transaction aborted — exactly what Postgres does, conservatively
    /// on SQL Server — so Commit is refused and only Rollback is left. The alternative is a COMMIT that
    /// Postgres silently turns into a ROLLBACK, which is the one outcome this feature may not have.
    /// </summary>
    [Fact]
    public async Task A_failed_statement_aborts_the_transaction_and_commit_is_refused()
    {
        var executor = new FakeExecutor();
        var h = NewHarness(executor: executor);
        var tab = Tab(h);
        var status = "";
        h.Ctx.Status = s => status = s;

        await h.Exec.ExecuteAsync("insert into t values (1)");
        executor.FailWith = "syntax error";
        await h.Exec.ExecuteAsync("insert into nope values (1)");

        Assert.Equal(TransactionState.Aborted, h.Ctx.Transactions.For(tab)!.State);
        Assert.False(h.Exec.CanCommit);
        Assert.True(h.Exec.HasOpenTransaction);          // still there, and still holding its connection
        Assert.True(h.Exec.TransactionIsStale);          // the chip says so without waiting for the idle clock
        // And it stops counting. "2 uncommitted" would describe work the user still has a choice about,
        // which is exactly what an aborted transaction has taken away.
        Assert.Equal("transaction aborted · 0s", h.Exec.TransactionText);

        await h.Exec.CommitTransactionAsync();
        Assert.Contains("can only be rolled back", status);
        Assert.NotNull(h.Ctx.Transactions.For(tab));     // refused, so nothing changed

        await h.Exec.RollbackTransactionAsync();
        Assert.Null(h.Ctx.Transactions.For(tab));
    }

    // ---- typed transaction control ----------------------------------------------------------------

    /// <summary>
    /// Bearing is holding the transaction, so a typed COMMIT would end it behind the driver's back. Refused
    /// for the whole batch, and nothing runs — including the INSERT in front of it.
    /// </summary>
    [Fact]
    public async Task A_typed_commit_is_refused_on_a_manual_commit_connection()
    {
        var h = NewHarness();
        var tab = Tab(h);
        var status = "";
        h.Ctx.Status = s => status = s;

        await h.Exec.ExecuteAsync("insert into t values (1); commit;");

        Assert.Contains("manual-commit mode", status);
        Assert.Contains("COMMIT", status);
        Assert.Null(h.Ctx.Transactions.For(tab));   // refused before anything opened, let alone ran
    }

    /// <summary>The refusal is manual-commit's alone: a script that manages its own transactions keeps
    /// working everywhere else, so nothing is narrowed (§1.2).</summary>
    [Fact]
    public async Task An_auto_commit_connection_still_runs_its_own_begin_and_commit()
    {
        var h = NewHarness(manualCommit: false);
        Tab(h);
        var status = "";
        h.Ctx.Status = s => status = s;

        await h.Exec.ExecuteAsync("begin; insert into t values (1); commit;");

        Assert.DoesNotContain("manual-commit", status);
    }

    /// <summary>
    /// The guard's verdict is generous on purpose — on T-SQL anything whose lead word is not a known read
    /// is treated as a write so it gets confirmed — and that is the wrong test for whether to open a
    /// transaction. <c>declare @id int; select …</c> is ordinary T-SQL for a read, and opening one on it
    /// would make browsing hold locks: the exact outcome "a read opens nothing" exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_T_SQL_read_behind_a_declare_opens_no_transaction()
    {
        var h = NewHarness();
        var tab = Tab(h, "sqlserver");
        // The same connection, on the other engine — the conservative arm is T-SQL's.
        h.Ctx.Project!.Manifest.Connections[0] = h.Conn with { ProviderId = "sqlserver" };

        await h.Exec.ExecuteAsync("declare @id int = 5; select * from payment where payment_id = @id;");
        Assert.Null(h.Ctx.Transactions.For(tab));

        // And a real write on the same connection still does open one.
        await h.Exec.ExecuteAsync("update payment set amount = 1 where payment_id = 5;");
        Assert.NotNull(h.Ctx.Transactions.For(tab));
    }

    // ---- switching mode without the dialog (#131) ---------------------------------------------------

    /// <summary>
    /// The pill flips the mode for the session. The saved connection is deliberately untouched:
    /// project.json is shared, and turning manual commit on for one dangerous UPDATE is not a statement
    /// about how everyone else should work.
    /// </summary>
    [Fact]
    public async Task The_toggle_changes_the_mode_in_force_and_not_the_saved_connection()
    {
        var h = NewHarness(manualCommit: false);
        var tab = Tab(h);
        Assert.False(h.Exec.IsManualCommit);
        Assert.Equal("Auto", h.Exec.CommitModeLabel);

        h.Exec.ToggleCommitMode();

        Assert.True(h.Exec.IsManualCommit);
        Assert.Equal("Manual •", h.Exec.CommitModeLabel);   // the dot: not what the connection is saved as
        Assert.True(h.Exec.CommitModeIsOverridden);
        Assert.False(h.Ctx.FindConnection(h.Conn.Id)!.ManualCommit);   // the file did not move

        // And it is not decoration: the next write opens a transaction.
        await h.Exec.ExecuteAsync("insert into t values (1)");
        Assert.NotNull(h.Ctx.Transactions.For(tab));
    }

    /// <summary>Flipping back to what was saved stops being an override rather than becoming a second
    /// one — so the pill's mark means "the dialog disagrees", never merely "you touched this".</summary>
    [Fact]
    public void Toggling_back_to_the_saved_mode_clears_the_override()
    {
        var h = NewHarness(manualCommit: false);
        Tab(h);

        h.Exec.ToggleCommitMode();
        Assert.True(h.Exec.CommitModeIsOverridden);
        h.Exec.ToggleCommitMode();

        Assert.False(h.Exec.IsManualCommit);
        Assert.False(h.Exec.CommitModeIsOverridden);
    }

    /// <summary>
    /// A toolbar pill may not be a thing that discards data or commits to production, and the mode is per
    /// connection while the transactions are per tab — so there is no coherent "and that one keeps waiting".
    /// Refusing costs one gesture and cannot lose anything.
    /// </summary>
    [Fact]
    public async Task Switching_to_auto_commit_is_refused_while_a_transaction_is_open()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");

        var status = "";
        h.Ctx.Status = s => status = s;
        h.Exec.ToggleCommitMode();

        Assert.True(h.Exec.IsManualCommit);                 // still manual
        Assert.NotNull(h.Ctx.Transactions.For(tab));        // and still holding
        Assert.Contains("commit or roll it back", status);
    }

    /// <summary>Saving the connection is the authoritative statement of what it is, so it wins over the
    /// pill. Without this rule, unticking the box would appear to do nothing.</summary>
    [Fact]
    public async Task Saving_the_connection_clears_a_session_override()
    {
        var h = NewHarness(manualCommit: false);
        Tab(h);
        h.Exec.ToggleCommitMode();
        Assert.True(h.Exec.IsManualCommit);

        await new ConnectionsViewModel(h.Ctx).AddOrUpdateConnectionAsync(h.Conn, password: null);

        Assert.False(h.Exec.IsManualCommit);
        Assert.False(h.Exec.CommitModeIsOverridden);
    }

    /// <summary>An override that had a transaction open, then unticked in the dialog: the mode in force was
    /// the override, so the drop still has to end the transaction (the saved record never said manual).</summary>
    [Fact]
    public async Task Unticking_the_box_ends_a_transaction_an_override_had_opened()
    {
        var h = NewHarness(manualCommit: false);
        var tab = Tab(h);
        h.Exec.ToggleCommitMode();
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        await new ConnectionsViewModel(h.Ctx).AddOrUpdateConnectionAsync(h.Conn, password: null);

        Assert.Equal(1, scope.Rollbacks);
        Assert.Null(h.Ctx.Transactions.For(tab));
    }

    // ---- the guards --------------------------------------------------------------------------------

    [Fact]
    public async Task Switching_database_is_refused_while_a_transaction_is_open()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");

        var status = "";
        h.Ctx.Status = s => status = s;
        var connections = new ConnectionsViewModel(h.Ctx);
        connections.SetTabDatabase(tab, "other");

        Assert.Equal("app", tab.DatabaseName ?? "app");
        Assert.Contains("uncommitted transaction", status);
        Assert.NotNull(h.Ctx.Transactions.For(tab));
    }

    [Fact]
    public async Task Switching_connection_is_refused_while_a_transaction_is_open()
    {
        var h = NewHarness();
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");

        var status = "";
        h.Ctx.Status = s => status = s;
        var connections = new ConnectionsViewModel(h.Ctx);
        connections.SetTabConnection(tab, Guid.NewGuid());

        Assert.Equal(h.Conn.Id, tab.ConnectionId);
        Assert.Contains("uncommitted transaction", status);
    }

    /// <summary>
    /// Disconnect is the path that would otherwise lose one invisibly: a held transaction keeps a lease, so
    /// evicting the connection only <i>retires</i> the session and the transaction survives with nothing on
    /// screen still admitting it exists.
    /// </summary>
    [Fact]
    public async Task Disconnect_asks_and_then_rolls_back()
    {
        var dialogs = new FakeDialogs();
        var h = NewHarness(dialogs: dialogs);
        var tab = Tab(h);
        // Connected through the toggle itself, as every other test of this command does: the link event is
        // raised off the connect thread, so a view-model that merely watched one would not have caught up.
        var connections = new ConnectionsViewModel(h.Ctx, dialogs);
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, connections.State);

        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        await connections.ToggleConnectionCommand.ExecuteAsync(null);   // now Disconnect

        var prompt = Assert.Single(dialogs.TransactionPrompts);
        Assert.Equal(1, prompt.Count);
        Assert.Contains("disconnect from prod", prompt.Action);
        Assert.Equal(1, scope.Rollbacks);
        Assert.Null(h.Ctx.Transactions.For(tab));
    }

    /// <summary>Answering "keep it open" has to stop the action, not merely delay it.</summary>
    [Fact]
    public async Task Refusing_the_prompt_keeps_the_transaction_and_stays_connected()
    {
        var dialogs = new FakeDialogs { DiscardTransactionsAnswer = false };
        var h = NewHarness(dialogs: dialogs);
        var tab = Tab(h);
        var connections = new ConnectionsViewModel(h.Ctx, dialogs);
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, connections.State);

        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        await connections.ToggleConnectionCommand.ExecuteAsync(null);   // Disconnect, and say no

        Assert.Equal(0, scope.Rollbacks);
        Assert.NotNull(h.Ctx.Transactions.For(tab));
        Assert.Equal(ConnectionState.Connected, connections.State);   // the action itself was cancelled
    }

    /// <summary>
    /// Closing the tab is the other way to lose one, and unlike the unsaved text beside it in the same
    /// prompt, uncommitted work cannot be written to session.json and restored — it lives on a server
    /// connection this tab is the only holder of.
    /// </summary>
    [Fact]
    public async Task Closing_the_tab_asks_and_then_rolls_back()
    {
        var dialogs = new FakeDialogs();
        var h = NewHarness(dialogs: dialogs);
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        var workspace = new WorkspaceViewModel(
            h.Ctx, new ScriptsViewModel(h.Ctx, () => { }), new ConnectionsViewModel(h.Ctx), dialogs);
        Assert.True(await workspace.CloseTabAsync(tab));

        Assert.Contains("close t", Assert.Single(dialogs.TransactionPrompts).Action);
        Assert.Equal(1, scope.Rollbacks);
        Assert.Null(h.Ctx.Transactions.For(tab));
    }

    [Fact]
    public async Task Refusing_the_close_prompt_keeps_both_the_tab_and_the_transaction()
    {
        var dialogs = new FakeDialogs { DiscardTransactionsAnswer = false };
        var h = NewHarness(dialogs: dialogs);
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);

        var workspace = new WorkspaceViewModel(
            h.Ctx, new ScriptsViewModel(h.Ctx, () => { }), new ConnectionsViewModel(h.Ctx), dialogs);
        Assert.False(await workspace.CloseTabAsync(tab));

        Assert.Equal(0, scope.Rollbacks);
        Assert.NotNull(h.Ctx.Transactions.For(tab));
        Assert.Contains(tab, h.Ctx.Tabs);
    }

    // ---- the idle clocks ----------------------------------------------------------------------------

    /// <summary>
    /// The chip turns amber on the warning threshold. Driven by backdating the scope's last activity rather
    /// than by waiting: the thresholds are minutes, and a test that waited for one would be a test that
    /// waited for five.
    /// </summary>
    [Fact]
    public async Task The_chip_goes_stale_once_the_transaction_has_been_idle_past_the_warning()
    {
        var h = NewHarness(settings: new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,
            IdleTransactionWarnMinutes = 5,
            IdleTransactionRollbackMinutes = 0,      // off, so only the warning is in play
        });
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        Assert.False(h.Exec.TransactionIsStale);

        ScopeOf(h, tab).Idle(TimeSpan.FromMinutes(6));
        await h.Exec.SweepTransactionsAsync();

        Assert.True(h.Exec.TransactionIsStale);
        Assert.NotNull(h.Ctx.Transactions.For(tab));   // warned, not ended
    }

    [Fact]
    public async Task An_abandoned_transaction_is_rolled_back_and_reported_as_a_toast()
    {
        var h = NewHarness(settings: new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,
            IdleTransactionWarnMinutes = 5,
            IdleTransactionRollbackMinutes = 15,
        });
        var tab = Tab(h);
        var toasts = new List<BackgroundCompletion>();
        h.Exec.BackgroundCompleted += toasts.Add;

        await h.Exec.ExecuteAsync("insert into t values (1)");
        var scope = ScopeOf(h, tab);
        scope.Idle(TimeSpan.FromMinutes(16));
        await h.Exec.SweepTransactionsAsync();

        Assert.Equal(1, scope.Rollbacks);
        Assert.Null(h.Ctx.Transactions.For(tab));
        // A toast even though this tab is the selected one: nobody was looking, which is the premise.
        var toast = Assert.Single(toasts);
        Assert.Contains("Rolled back", toast.Message);
        Assert.Contains("idle", toast.Message);
    }

    /// <summary>Zero is off, and off has to mean a transaction is left alone indefinitely — the locks are
    /// then the user's to watch, which the setting's own description says.</summary>
    [Fact]
    public async Task A_zero_rollback_threshold_never_ends_one()
    {
        var h = NewHarness(settings: new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,
            IdleTransactionWarnMinutes = 0,
            IdleTransactionRollbackMinutes = 0,
        });
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        ScopeOf(h, tab).Idle(TimeSpan.FromDays(1));

        await h.Exec.SweepTransactionsAsync();

        Assert.NotNull(h.Ctx.Transactions.For(tab));
        Assert.False(h.Exec.TransactionIsStale);
    }

    /// <summary>
    /// A rollback threshold below the warning is a settings file nobody meant: it would roll back before it
    /// warned, making the warning a sentence about something already over. Raised to the warning instead.
    /// </summary>
    [Fact]
    public async Task A_rollback_threshold_under_the_warning_is_raised_to_it()
    {
        var h = NewHarness(settings: new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,
            IdleTransactionWarnMinutes = 10,
            IdleTransactionRollbackMinutes = 2,
        });
        var tab = Tab(h);
        await h.Exec.ExecuteAsync("insert into t values (1)");
        ScopeOf(h, tab).Idle(TimeSpan.FromMinutes(5));   // past 2, short of 10

        await h.Exec.SweepTransactionsAsync();

        Assert.NotNull(h.Ctx.Transactions.For(tab));
    }

    // ---- the query log ------------------------------------------------------------------------------

    /// <summary>
    /// §1.6's question — "what ran against production last week" — is a worse answer if it cannot say
    /// whether what ran was kept. The statement and the commit that ended it carry the same id.
    /// </summary>
    [Fact]
    public async Task The_statement_and_its_commit_share_a_transaction_id_in_the_log()
    {
        var h = NewHarness();
        var tab = Tab(h);

        await h.Exec.ExecuteAsync("insert into t values (1)");
        var id = h.Ctx.Transactions.For(tab)!.Id;
        await h.Exec.CommitTransactionAsync();

        var rows = await Eventually(h.Ctx.QueryLog, 2);
        Assert.Equal(["commit", "insert into t values (1)"], rows.Select(r => r.SqlText));
        Assert.All(rows, r => Assert.Equal(id, r.TransactionId));
    }

    /// <summary>Null on an ordinary run, and that is not the same as "rolled back" — which is why a report
    /// says how many rows it could not place rather than calling them uncommitted.</summary>
    [Fact]
    public async Task An_auto_commit_run_logs_no_transaction_id()
    {
        var h = NewHarness(manualCommit: false);
        Tab(h);

        await h.Exec.ExecuteAsync("select 1");

        var rows = await Eventually(h.Ctx.QueryLog, 1);
        Assert.Null(Assert.Single(rows).TransactionId);
    }

    /// <summary>The log's writer is a background one (#78), so a read right after Append races the insert.</summary>
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
