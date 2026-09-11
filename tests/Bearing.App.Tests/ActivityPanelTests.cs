using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The server activity panel (#101) — polling, what it says, and what it does to a backend.
/// <para>
/// All of it is derived from a scripted <c>IServerActivity</c>, so it is testable without a server or a
/// window. That the queries themselves work is <c>ServerActivityTests</c> in Bearing.Data.Tests, live.
/// </para>
/// </summary>
public class ActivityPanelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-activity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <param name="seconds">
    /// How long the backend has been doing what it is doing. It reaches <c>RunningFor</c> only for an
    /// <c>active</c> row and <c>StateFor</c> always — which is how the server answers, since
    /// <c>query_start</c> is guarded on the state and <c>state_change</c> is not.
    /// </param>
    private static BackendActivity Backend(int pid, bool ours = false, string? query = "select 1",
        string? user = "app", string? state = "active", double? seconds = 3) => new(
        Pid: pid,
        BackendStart: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
        User: user,
        Database: "app",
        Application: ours ? "bearing" : "psql",
        State: state,
        WaitEvent: null,
        RunningFor: seconds is { } s && state == "active" ? TimeSpan.FromSeconds(s) : null,
        StateFor: seconds is { } t ? TimeSpan.FromSeconds(t) : null,
        Query: query,
        IsOurs: ours);

    /// <summary>A context with one connection and one tab on it. <paramref name="live"/> false leaves the
    /// connection with no session, which is the "nothing to read" path.</summary>
    private (WorkspaceContext Ctx, FakeActivity Activity, FakeDialogs Dialogs, ConnectionInfo Conn) NewPanel(
        bool live = true, bool readOnly = false)
    {
        Directory.CreateDirectory(_root);
        var activity = new FakeActivity();
        var provider = new FakeProvider { Activity = activity };
        var ctx = new WorkspaceContext(
            provider,
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore());

        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "prod-eu",
            ProviderId = "postgres",
            Database = "app",
            User = "reporting",
            ReadOnly = readOnly,
        };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };

        var tab = new EditorTabViewModel("t1") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;

        if (live) ctx.Sessions.GetOrConnectAsync(conn, CancellationToken.None).GetAwaiter().GetResult();

        return (ctx, activity, new FakeDialogs(), conn);
    }

    // ---- reading -------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_lists_the_backends_and_counts_them()
    {
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(101, ours: true), Backend(202)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        await panel.RefreshAsync();

        Assert.Equal(2, panel.Backends.Count);
        Assert.Equal([101, 202], panel.Backends.Select(b => b.Pid));
        Assert.Contains("2 sessions", panel.Status);
    }

    [Fact]
    public async Task A_role_that_cannot_see_other_sessions_is_told_so_rather_than_shown_a_quiet_server()
    {
        // The mistake this panel must not make, and §1.7's rule in a new place. A restricted role does not
        // receive other sessions' rows at all, so "1 session" on its own would be a claim about the server
        // that nothing checked.
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(101, ours: true)];
        activity.SeesAllSessions = false;
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        await panel.RefreshAsync();

        Assert.Contains("of your own", panel.Status);
        Assert.Contains("reporting", panel.Status);       // names the role that cannot see them
        Assert.DoesNotContain("No running sessions", panel.Status);
    }

    [Fact]
    public async Task An_empty_server_and_a_blind_role_do_not_read_the_same()
    {
        var (ctx, activity, dialogs, _) = NewPanel();
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        activity.Backends = [];
        activity.SeesAllSessions = true;
        await panel.RefreshAsync();
        var quiet = panel.Status;

        activity.SeesAllSessions = false;
        await panel.RefreshAsync();
        var blind = panel.Status;

        Assert.NotEqual(quiet, blind);
        Assert.Contains("No running sessions", quiet);
        Assert.Contains("can't see other roles", blind);
    }

    [Fact]
    public async Task With_no_live_session_nothing_is_read_and_the_panel_says_why()
    {
        // §1.5's rule: a panel that refreshes itself must never be the reason a connection is opened.
        var (ctx, activity, dialogs, _) = NewPanel(live: false);
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        await panel.RefreshAsync();

        Assert.Equal(0, activity.Reads);
        Assert.Empty(panel.Backends);
        Assert.Contains("prod-eu", panel.Status);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_rows_it_had_and_says_the_reading_is_old()
    {
        // Clearing on a blip would make a row blink out from under a pointer on its way to it — and this list
        // refreshes every couple of seconds.
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(101), Backend(202)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        panel.SetPolling(true);
        await panel.RefreshAsync();
        Assert.Equal(2, panel.Backends.Count);

        activity.Throws = new InvalidOperationException("server went away");
        await panel.RefreshAsync();

        Assert.Equal(2, panel.Backends.Count);            // the last good answer stands
        Assert.Contains("last reading", panel.Status);
    }

    [Fact]
    public async Task A_failed_first_read_says_it_could_not_read_rather_than_that_there_is_nothing()
    {
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Throws = new InvalidOperationException("boom");
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        panel.SetPolling(true);

        await panel.RefreshAsync();

        Assert.Empty(panel.Backends);
        Assert.Contains("Couldn't read", panel.Status);
    }

    [Fact]
    public async Task The_selected_row_survives_a_refresh_that_reorders_the_list()
    {
        // The list is ordered by how long each statement has been running, so it reorders under the user
        // constantly. Re-finding by index would move the selection to a different session.
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(101), Backend(202), Backend(303)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        panel.Selected = panel.Backends.Single(b => b.Pid == 202);

        activity.Backends = [Backend(303), Backend(202), Backend(101)];
        await panel.RefreshAsync();

        Assert.Equal(202, panel.Selected?.Pid);
    }

    [Fact]
    public async Task A_session_that_is_still_there_keeps_its_row_object()
    {
        // The list used to be cleared and rebuilt on every poll, which removed the selected item — so the
        // list control wrote Selected = null back through its binding before the new rows existed, and the
        // SQL pane below (which follows the selection) blanked and re-formatted itself twice every 2.5
        // seconds, taking the caret, the selection and the scroll with it.
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(101), Backend(202)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        var row = panel.Backends.Single(b => b.Pid == 202);
        panel.Selected = row;

        var selectionChanges = 0;
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivityPanelViewModel.Selected)) selectionChanges++;
        };

        // The same two sessions, one of them a little further into its statement — and reordered, because
        // that is what the list does.
        activity.Backends = [Backend(202, seconds: 9), Backend(101, seconds: 4)];
        await panel.RefreshAsync();

        Assert.Same(row, panel.Selected);
        Assert.Same(row, panel.Backends[0]);
        Assert.Equal(0, selectionChanges);

        // Kept, not frozen: the row carries the new reading and says so, or the list would show a duration
        // that stopped advancing.
        Assert.Contains("9", row.Header);
        Assert.Equal(TimeSpan.FromSeconds(9), row.Elapsed);
    }

    [Fact]
    public async Task A_row_that_starts_running_something_else_says_so_without_being_replaced()
    {
        // The other half of keeping the object: the pane and the list both read through the row, so a
        // backend that moved on to another statement has to notify rather than be swapped out.
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(101, query: "select 1")];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        var row = panel.Backends.Single();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        activity.Backends = [Backend(101, query: "vacuum analyze payment")];
        await panel.RefreshAsync();

        Assert.Same(row, panel.Backends.Single());
        Assert.Equal("vacuum analyze payment", row.Query);
        Assert.Contains(nameof(BackendRowViewModel.Query), changed);
    }

    [Fact]
    public async Task A_reused_pid_is_not_the_same_row()
    {
        // A pid alone is not identity — the server reuses them. Keeping the selection on a pid whose backend
        // has been replaced would point the Terminate menu at a session the user never chose.
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(101)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();
        panel.Selected = panel.Backends.Single();

        activity.Backends =
        [
            Backend(101) with { BackendStart = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero) },
        ];
        await panel.RefreshAsync();

        Assert.Null(panel.Selected);
    }

    [Fact]
    public async Task A_row_that_disappeared_clears_the_selection()
    {
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(101), Backend(202)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();
        panel.Selected = panel.Backends.Single(b => b.Pid == 202);

        activity.Backends = [Backend(101)];
        await panel.RefreshAsync();

        Assert.Null(panel.Selected);
    }

    [Fact]
    public async Task The_read_is_scoped_to_this_tab_s_database_until_asked_to_widen()
    {
        // pg_stat_activity is cluster-wide, so an unscoped read returns every session on the host — other
        // databases, other roles, other applications, and the statement each is running. That is the answer
        // you want when hunting a lock holder and not what a panel should show unasked.
        var (ctx, activity, dialogs, conn) = NewPanel();
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        await panel.RefreshAsync();
        Assert.Equal(conn.Database, activity.Filters.Single().Database);

        panel.WholeServer = true;
        await WaitUntil(() => activity.Filters.Count > 1);

        // Null is the whole server, and it only happens because it was asked for.
        Assert.Null(activity.Filters.Last().Database);
    }

    [Fact]
    public async Task The_status_line_says_which_scope_it_is_describing()
    {
        // "2 sessions" is a different claim about a database than about a server.
        var (ctx, activity, dialogs, conn) = NewPanel();
        activity.Backends = [Backend(101)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        await panel.RefreshAsync();
        Assert.Contains(conn.Database, panel.Status);

        panel.WholeServer = true;
        await WaitUntil(() => panel.Status.Contains("this server"));
        Assert.Contains("this server", panel.Status);
    }

    [Fact]
    public async Task Idle_sessions_are_left_out_until_asked_for()
    {
        // A production database is mostly an application server's pool between statements: nothing on those
        // backends to cancel, and enough of them to bury the ones doing something.
        var (ctx, activity, dialogs, _) = NewPanel();
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        await panel.RefreshAsync();
        Assert.False(activity.Filters.Single().IncludeIdle);

        panel.IncludeIdle = true;
        await WaitUntil(() => activity.Filters.Count > 1);
        Assert.True(activity.Filters.Last().IncludeIdle);
    }

    [Fact]
    public void An_idle_in_transaction_backend_is_not_an_idle_one()
    {
        // The asymmetry that matters. It holds locks, it is the state worth noticing on a real server, and
        // it is the row this panel most exists to show — so the tidy-up that hides idle must not take it.
        Assert.NotEqual("idle", Backend(1, state: "idle in transaction").State);

        // And the demo fixture keeps one, so the default view is never empty of the interesting case.
        var demo = Bearing.Demo.DemoCatalog.Activity();
        Assert.Contains(demo, b => b.State == "idle in transaction");
        Assert.Contains(demo, b => b.State == "idle");
    }

    // ---- polling -------------------------------------------------------------------------------

    [Fact]
    public async Task Polling_reads_once_immediately_rather_than_waiting_for_the_first_tick()
    {
        var (ctx, activity, dialogs, _) = NewPanel();
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        panel.SetPolling(true);
        await WaitUntil(() => activity.Reads > 0);

        Assert.True(panel.IsPolling);
        Assert.Equal(1, activity.Reads);
    }

    [Fact]
    public void Stopping_polling_is_idempotent_and_starting_twice_does_not_double_read()
    {
        // The shell calls this from two change handlers, so it is called more often than the state changes.
        var (ctx, activity, dialogs, _) = NewPanel();
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        panel.SetPolling(false);
        Assert.False(panel.IsPolling);
        Assert.Equal(0, activity.Reads);

        panel.SetPolling(true);
        panel.SetPolling(true);
        Assert.True(panel.IsPolling);
        Assert.True(activity.Reads <= 1);
    }

    [Fact]
    public async Task A_read_still_running_is_not_joined_by_another()
    {
        // Two reads of a moving server answer the same question; the later one is the one worth waiting for.
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Gate = new TaskCompletionSource();
        var panel = new ActivityPanelViewModel(ctx, dialogs);

        var first = panel.RefreshAsync();
        await WaitUntil(() => activity.Reads == 1);
        await panel.RefreshAsync();                        // returns at once, does not queue

        Assert.Equal(1, activity.Reads);
        activity.Gate.SetResult();
        await first;
    }

    // ---- acting --------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_confirms_first_and_names_the_backend()
    {
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(4242, user: "etl")];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        dialogs.BackendActionAnswer = true;
        await panel.CancelBackendAsync(panel.Backends.Single());

        var asked = Assert.Single(dialogs.BackendActions);
        Assert.Equal(BackendActionKind.Cancel, asked.Kind);
        Assert.Contains("4242", asked.Heading);
        Assert.Contains("etl", asked.Target);
        Assert.Equal([4242], activity.Cancelled);
    }

    [Fact]
    public async Task Declining_the_confirmation_does_nothing_to_the_backend()
    {
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(4242)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        dialogs.BackendActionAnswer = false;
        await panel.TerminateBackendAsync(panel.Backends.Single());

        Assert.Single(dialogs.BackendActions);
        Assert.Empty(activity.Terminated);
    }

    [Fact]
    public async Task Terminating_confirms_and_then_terminates()
    {
        var (ctx, activity, dialogs, _) = NewPanel();
        activity.Backends = [Backend(4242)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        dialogs.BackendActionAnswer = true;
        await panel.TerminateBackendAsync(panel.Backends.Single());

        Assert.Equal(BackendActionKind.Terminate, Assert.Single(dialogs.BackendActions).Kind);
        Assert.Equal([4242], activity.Terminated);
    }

    [Fact]
    public async Task A_read_only_connection_refuses_terminate_without_even_asking()
    {
        // Refused before it is confirmed: a prompt for something that will not be done is a question with no
        // answer. And this refusal has to be ours — pg_terminate_backend is a function call, not a
        // transactional write, so default_transaction_read_only lets it straight through (#99 / #101).
        var (ctx, activity, dialogs, _) = NewPanel(readOnly: true);
        activity.Backends = [Backend(4242)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        await panel.TerminateBackendAsync(panel.Backends.Single());

        Assert.Empty(dialogs.BackendActions);
        Assert.Empty(activity.Terminated);
    }

    [Fact]
    public async Task A_read_only_connection_still_cancels()
    {
        // The asymmetry is the decision, not an oversight: cancelling stops a statement and leaves the
        // session, and it is most wanted on exactly the production connection most likely to be read-only.
        var (ctx, activity, dialogs, _) = NewPanel(readOnly: true);
        activity.Backends = [Backend(4242)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        dialogs.BackendActionAnswer = true;
        await panel.CancelBackendAsync(panel.Backends.Single());

        Assert.Equal([4242], activity.Cancelled);
    }

    [Fact]
    public async Task A_session_that_went_away_while_the_confirmation_was_open_is_not_acted_on()
    {
        // A confirmation is modal and can sit open for minutes. In that time the idle sweep, a Disconnect or
        // a connection edit can dispose the session that was live when the menu was opened — and Lease does
        // no validity check, so acting on the captured one surfaces as an ObjectDisposedException instead of
        // doing anything. The session is resolved again after the dialog returns.
        var (ctx, activity, dialogs, conn) = NewPanel();
        activity.Backends = [Backend(4242)];
        var panel = new ActivityPanelViewModel(ctx, dialogs);
        await panel.RefreshAsync();

        dialogs.BackendActionAnswer = true;
        dialogs.WhileConfirmingBackendAction =
            () => ctx.Sessions.EvictConnectionAsync(conn.Id).GetAwaiter().GetResult();

        await panel.TerminateBackendAsync(panel.Backends.Single());

        // Asked, and then found there was nothing live to ask it of — so nothing was sent.
        Assert.Single(dialogs.BackendActions);
        Assert.Empty(activity.Terminated);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
    }
}
