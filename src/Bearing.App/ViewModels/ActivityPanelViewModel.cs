using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Connections;
using Bearing.App.Services;
using Bearing.App.Workspace;
using Bearing.Core.Data;

namespace Bearing.App.ViewModels;

/// <summary>
/// The server activity panel (#101): this connection's backends, refreshed while the panel is on screen, with
/// Cancel and Terminate on a row.
/// <para>
/// It answers the question 0.5.3 opened and could not close — a query that outran its timeout is told it "may
/// still be running on the server", and until now there was no way to look.
/// </para>
/// <para>
/// The register is §1.5's, and for the same reasons. It <b>never connects</b>: the read goes through an
/// already-live session, so a panel refreshing itself can never be the reason a credential prompt appears. It
/// <b>never blocks</b>: every read carries a deadline shorter than the interval, so a slow server costs
/// refreshes rather than piling work up. And it <b>never guesses</b>: what the role could not see is reported
/// as not seen, never as an empty server.
/// </para>
/// </summary>
public sealed partial class ActivityPanelViewModel : ObservableObject
{
    /// <summary>How often the panel re-reads while it is visible. Fast enough that a hung query is watchable,
    /// slow enough that the cost is one small catalog read every few seconds on one connection.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);

    /// <summary>The deadline on one read — deliberately under <see cref="PollInterval"/>, so a server that
    /// cannot answer in time degrades into fewer refreshes rather than a queue of overlapping ones.</summary>
    public static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(2);

    private readonly WorkspaceContext _ctx;
    private readonly IDialogService? _dialogs;

    private readonly DispatcherTimer _timer;

    public ActivityPanelViewModel(WorkspaceContext ctx, IDialogService? dialogs = null)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        // The cadence lives with the panel rather than the shell: the shell's job is saying whether this is
        // on screen, not how often a server should be asked.
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>The backends, newest read wins. Rebuilt per poll; see <see cref="Apply"/> for what survives.</summary>
    public ObservableCollection<BackendRowViewModel> Backends { get; } = [];

    [ObservableProperty] private BackendRowViewModel? _selected;

    /// <summary>
    /// One line carrying count, empty, restricted, disconnected, stale and error. A panel that shows a bare
    /// empty list with nothing explaining it is the failure mode here, because "no sessions" and "you may not
    /// see them" look identical.
    /// </summary>
    [ObservableProperty] private string _status = "";

    /// <summary>
    /// Whether to list every database on the server, or only the one this tab is on.
    /// <para>
    /// Off by default, and that is the safer default rather than the more useful one. <c>pg_stat_activity</c>
    /// is cluster-wide, so the whole-server read returns other databases, other roles, other applications and
    /// the statement each is running — which is what you want when hunting a lock holder, and not what a
    /// panel opened beside your own work should show unasked.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _wholeServer;

    partial void OnWholeServerChanged(bool value) => _ = RefreshAsync();

    /// <summary>True while the panel should be re-reading — i.e. it is the active panel and the pane is open.
    /// The shell sets this; the panel does not know about the rail.</summary>
    public bool IsPolling { get; private set; }

    private bool _reading;
    private CancellationTokenSource? _inFlight;

    /// <summary>
    /// Start or stop polling. Called by the shell whenever the active panel or the pane's openness changes —
    /// both, because <c>[ObservableProperty]</c>'s setter short-circuits on an unchanged value and collapsing
    /// the pane from this panel's own rail tile then re-opening it never changes <c>ActivePanel</c>.
    /// </summary>
    public void SetPolling(bool polling)
    {
        if (IsPolling == polling) return;
        IsPolling = polling;

        if (!polling)
        {
            _timer.Stop();
            // Cancel whatever is in flight rather than letting it land on a panel nobody is looking at.
            var superseded = _inFlight;
            _inFlight = null;
            superseded?.Cancel();
            superseded?.Dispose();
            return;
        }

        // Read once immediately rather than showing an empty panel until the first tick — the shape
        // ExecutionViewModel's elapsed timer already uses when it starts.
        _ = RefreshAsync();
        _timer.Start();
    }

    /// <summary>One poll. Safe to call on a tick, on open, and from a refresh button.</summary>
    public async Task RefreshAsync()
    {
        // A read still running when the next tick arrives is skipped, not queued: two reads of a moving
        // server answer the same question, and the later one is the one worth waiting for.
        if (_reading) return;

        if (Target() is not { } target)
        {
            Backends.Clear();
            Selected = null;
            Status = NotConnectedText();
            return;
        }

        var (info, session) = target;
        _reading = true;
        var cts = new CancellationTokenSource(PollTimeout);
        _inFlight = cts;
        try
        {
            // Leased for this read only, rather than for as long as the panel is open: a lease outstanding
            // across ticks would also keep a *retired* session alive, so an evict or a database switch could
            // not finish while the panel sat there.
            //
            // It does not stop an open panel keeping the pool warm, and nothing here should: leasing and
            // releasing both stamp LastUsedUtc, so a session read every 2.5 s never goes idle. That is the
            // right answer — sweeping a pool that is being queried continuously would only make the next tick
            // rebuild it — and the bound on it is that polling stops the moment the panel is not on screen.
            using var lease = _ctx.Sessions.Lease(session);
            var activity = await lease.Session.Activity.GetActivityAsync(
                WholeServer ? null : info.Database, cts.Token);
            Apply(activity, info);
        }
        catch (OperationCanceledException)
        {
            // Timed out, or the panel was hidden mid-read. Either way the rows on screen are the last good
            // answer and stay — clearing them would make a row blink out from under a pointer aiming at it.
            if (IsPolling) Status = StaleText("the server did not answer in time");
        }
        catch (Exception ex)
        {
            if (IsPolling) Status = StaleText(SafeErrorText.Of(ex));
        }
        finally
        {
            _reading = false;
            if (ReferenceEquals(_inFlight, cts)) _inFlight = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Fold a fresh read into the list, keeping the user's selection.
    /// <para>
    /// Re-found by identity rather than by index, because the list reorders (it is sorted by how long each
    /// statement has been running) and it refreshes every couple of seconds while a pointer may be on its way
    /// to a row. Identity is pid <em>plus</em> when the backend connected: a pid on its own is reused by the
    /// server, and reusing it here would silently move the selection to a different session.
    /// </para>
    /// </summary>
    private void Apply(ServerActivity activity, ConnectionInfo info)
    {
        var keep = Selected?.Identity;

        Backends.Clear();
        foreach (var backend in activity.Backends) Backends.Add(new BackendRowViewModel(backend));

        Selected = keep is null ? null : Backends.FirstOrDefault(b => b.Identity == keep);
        Status = DescribeText(activity, info);
    }

    /// <summary>The selected tab's connection and its <em>already live</em> session, or null.</summary>
    private (ConnectionInfo Info, ConnectionSession Session)? Target()
    {
        if (_ctx.SelectedTab is not { } tab) return null;
        if (_ctx.EffectiveConnection(tab) is not { } info) return null;
        // TryGet, never GetOrConnect: a panel that refreshes itself must not be able to raise a credential
        // prompt behind a rail click (§1.5).
        return _ctx.Sessions.TryGet(SessionKey.For(info)) is { } session ? (info, session) : null;
    }

    /// <summary>
    /// Why there is nothing to show. Reads the <em>server link</em> rather than the pool, because §9.4 is
    /// explicit that no user-facing connected/disconnected indicator reads <c>TryGet</c> — a swept pool is not
    /// a disconnection, and saying so here would contradict the beacon sitting a few pixels away.
    /// </summary>
    private string NotConnectedText()
    {
        if (_ctx.SelectedTab is not { } tab || _ctx.EffectiveConnection(tab) is not { } info)
            return "No connection on this tab.";

        return _ctx.Sessions.IsLinked(info.Id)
            ? $"Nothing open on {info.Database} yet — run a query to see this server's sessions."
            : $"Not connected to {info.Name}.";
    }

    /// <summary>
    /// What the read found. The restricted case is the one that matters: a role without
    /// <c>pg_read_all_stats</c> does not receive other sessions' rows at all, so an unqualified "1 session"
    /// would be a claim about the server that nobody checked (§1.7's rule, one panel later).
    /// </summary>
    private string DescribeText(ServerActivity activity, ConnectionInfo info)
    {
        var count = activity.Backends.Count;
        var sessions = count == 1 ? "1 session" : $"{count} sessions";
        // Named, because "2 sessions" is a different claim about a database than about a server.
        var where = WholeServer ? "on this server" : $"on {info.Database}";

        if (!activity.SeesAllSessions)
            return count == 0
                ? $"No other sessions of your own {where}. {info.User} can't see other roles' sessions."
                : $"{sessions} of your own {where}. {info.User} can't see other roles' sessions.";

        return count == 0 ? $"No other sessions {where}." : $"{sessions} {where}.";
    }

    // ---- acting on a backend --------------------------------------------------------------------

    /// <summary>Stop the statement this backend is running. Confirmed, and allowed on a read-only
    /// connection — see <see cref="WriteRefusal.ReasonForTerminate"/> for why the two differ.</summary>
    public Task CancelBackendAsync(BackendRowViewModel row) => ActAsync(row, BackendActionKind.Cancel);

    /// <summary>End this backend's session.</summary>
    public Task TerminateBackendAsync(BackendRowViewModel row) => ActAsync(row, BackendActionKind.Terminate);

    private async Task ActAsync(BackendRowViewModel row, BackendActionKind kind)
    {
        if (Target() is not { } target) { _ctx.SetStatus(NotConnectedText()); return; }
        var info = target.Info;

        // Refused before it is confirmed: a prompt for something that will not be done is a question with no
        // answer. Terminate only — cancelling a statement is not a change to the server, and is the one thing
        // you most want on a read-only production connection.
        if (kind == BackendActionKind.Terminate && WriteRefusal.ReasonForTerminate(info) is { } refused)
        {
            _ctx.SetStatus(refused);
            return;
        }

        var request = new BackendAction(kind, info, row.Backend);
        if (_dialogs is not { } dialogs || !await dialogs.ConfirmBackendActionAsync(request))
        {
            _ctx.SetStatus($"Cancelled — backend {row.Pid} left alone.");
            return;
        }

        // Resolved again, after the dialog rather than before it. A confirmation is modal and can sit there
        // for minutes, and in that time the idle sweep, a Disconnect or a connection edit can all dispose the
        // session that was live when the menu was opened — Lease does no validity check, so acting on the
        // captured one surfaces as an ObjectDisposedException instead of doing anything. The same reason
        // SaveChangesAsync takes its lease after its confirmation and not before.
        if (Target() is not { } current) { _ctx.SetStatus(NotConnectedText()); return; }

        try
        {
            // Leased for the call, like the read: the pool must not be swept out from under it.
            using var lease = _ctx.Sessions.Lease(current.Session);
            var acted = kind == BackendActionKind.Cancel
                ? await lease.Session.Activity.CancelBackendAsync(row.Pid, CancellationToken.None)
                : await lease.Session.Activity.TerminateBackendAsync(row.Pid, CancellationToken.None);

            _ctx.SetStatus(acted ? request.DoneText : request.RefusedText);
        }
        catch (Exception ex)
        {
            _ctx.SetStatus($"Backend {row.Pid}: {SafeErrorText.Of(ex)}");
        }

        // Show the result rather than waiting up to a full interval for the next tick to reveal it.
        await RefreshAsync();
    }

    private string StaleText(string reason) => Backends.Count == 0
        ? $"Couldn't read server activity — {reason}."
        : $"Showing the last reading — {reason}.";
}

/// <summary>
/// One backend as the panel lists it. A plain immutable projection, like <c>HistoryRowViewModel</c>: the list
/// is rebuilt per poll, so a row never changes and never needs to notify.
/// </summary>
public sealed class BackendRowViewModel
{
    public BackendRowViewModel(BackendActivity backend) => Backend = backend;

    public BackendActivity Backend { get; }

    /// <summary>What makes this row the same row across a refresh. A pid alone is not enough — the server
    /// reuses them.</summary>
    public (int Pid, DateTimeOffset? Since) Identity => (Backend.Pid, Backend.BackendStart);

    public int Pid => Backend.Pid;
    public bool IsOurs => Backend.IsOurs;

    /// <summary>pid · state · how long, which is the line you scan down.</summary>
    public string Header
    {
        get
        {
            var parts = new List<string> { Backend.Pid.ToString() };
            if (!string.IsNullOrWhiteSpace(Backend.State)) parts.Add(Backend.State!);
            if (Backend.RunningFor is { } elapsed) parts.Add(BackendAction.FormatElapsed(elapsed));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The statement, flattened to one line. Empty when the backend is running nothing — an idle
    /// session shows its last query, and a blank line is the honest rendering of no statement at all.</summary>
    public string Query => OneLine(Backend.Query);

    /// <summary>Everything the row cannot fit, for the detail pane below the list.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Backend.User)) parts.Add($"role {Backend.User}");
            if (!string.IsNullOrWhiteSpace(Backend.Database)) parts.Add($"database {Backend.Database}");
            if (!string.IsNullOrWhiteSpace(Backend.Application)) parts.Add(Backend.Application!);
            if (!string.IsNullOrWhiteSpace(Backend.WaitEvent)) parts.Add($"waiting on {Backend.WaitEvent}");
            if (Backend.IsOurs) parts.Add("opened by Bearing");
            return string.Join(" · ", parts);
        }
    }

    internal static string OneLine(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "";
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 160 ? flat : flat[..159] + "…";
    }
}
