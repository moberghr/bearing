using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Connections;
using Bearing.App.Results;
using Bearing.App.Services;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Explain;
using Bearing.Core.Logging;
using Bearing.Core.Schema;
using Bearing.Results;
using Bearing.Sessions;
using Bearing.Sql;

namespace Bearing.App.ViewModels;

/// <summary>
/// A run that finished on a tab the user wasn't looking at. Its terminal message would otherwise be
/// written to a status bar describing a different tab, so it is raised as a toast instead.
/// </summary>
/// <param name="TabName">Header of the tab the run belonged to.</param>
/// <param name="Message">The status line the run would have posted (summary, error, or cancellation).</param>
/// <param name="TabStillOpen">False when the tab was closed before the run finished — the results were
/// dropped and this notification is all that's left of it. Switching projects does <b>not</b> close a tab
/// (it parks it), so a background project's run still reports true here.</param>
/// <param name="Tab">The tab itself, so the view can bring it back on screen when the toast is clicked —
/// switching project first if it belongs to one that isn't showing. Null only when the tab is gone.</param>
public sealed record BackgroundCompletion(string TabName, string Message, bool TabStillOpen, EditorTabViewModel? Tab = null);

/// <summary>
/// A finished export. Raised rather than acted on because the useful follow-up — offering to open the
/// containing folder — is a notification, and the view owns those (§2.3).
/// </summary>
/// <param name="Path">The file that was written.</param>
/// <param name="RowCount">Rows in it, excluding the header.</param>
public sealed record ExportCompletion(string Path, int RowCount, ExportFormat Format);

/// <summary>
/// The execution concern: running the selected tab's SQL, paging, count, foreign-key navigation, and the
/// inline-edit save/discard. Owns the in-flight cancellation and the busy flag. Extracted from the
/// shell (docs/mvvm-refactor-plan.md phase 3); coordinates through <see cref="WorkspaceContext"/> and reads
/// the selected tab from it (moved into the context in phase 4). Pure DML/result shaping stays in Results/
/// (ResultEditModel, ResultSetBuilder).
/// </summary>
public sealed partial class ExecutionViewModel : ObservableObject
{
    /// <summary>Page size: first page and each "load more" fetch this many rows. Read per use (not
    /// cached) so a change in the settings window applies to the next run without a restart; a result set
    /// already on screen keeps the page size it was built with, which is why the setting says so.</summary>
    public int PageSize => _ctx.Settings.ResultPageSize;

    private readonly WorkspaceContext _ctx;
    private readonly IDialogService? _dialogs;
    private EditorTabViewModel? _watchedTab;

    // Ticks on the UI thread while the selected tab is running, refreshing ElapsedText from that tab's
    // run clock so the status bar shows a live execution timer. Stopped whenever nothing is in flight.
    private readonly DispatcherTimer _elapsedTimer;

    // Runs only while a transaction is open: warns, and eventually rolls one back (#131).
    private readonly DispatcherTimer _transactionTimer;

    public ExecutionViewModel(WorkspaceContext ctx, IDialogService? dialogs)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _elapsedTimer.Tick += (_, _) => RefreshElapsed();
        // IsBusy / RunButtonText / CancelExecution are a façade over the *selected* tab's per-tab run
        // state (execution itself is per-tab). Track selection changes and the selected tab's IsRunning
        // so the toolbar Run/Cancel button reflects whichever tab is focused.
        _ctx.SelectedTabChanged += OnSelectedTabChanged;
        // The chip and the two commands are a façade over the selected tab's transaction, exactly as
        // IsBusy is over its run — so both the selection changing and the map changing have to refresh it.
        _ctx.Transactions.Changed += OnTransactionsChanged;
        _ctx.CommitModes.Changed += RefreshCommitMode;
        // The two idle clocks (#131). One timer for every open transaction, running only while there is at
        // least one — a tick that finds nothing to do is a tick nobody asked for. Five seconds because the
        // chip shows seconds below a minute; the thresholds themselves are minutes.
        _transactionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _transactionTimer.Tick += (_, _) => CrashReporter.Observe(SweepTransactionsAsync(), "transactions.sweep");
        OnSelectedTabChanged();
    }

    // ---- manual-commit mode (#131) ------------------------------------------------------------

    /// <summary>The selected tab's open transaction, or null when it has none.</summary>
    public TabTransaction? Transaction => Selected is { } tab ? _ctx.Transactions.For(tab) : null;

    /// <summary>Whether the selected tab is holding one — what the status chip's visibility binds to.</summary>
    public bool HasOpenTransaction => Transaction is not null;

    /// <summary>
    /// Whether it may still be committed: there is one, nothing in it failed, and the tab is not mid-statement.
    /// <para>
    /// The last of those is not politeness. A transaction lives on <b>one</b> physical connection, and so
    /// does the statement running in it — issuing a commit on a connection with a command in flight throws
    /// from the driver, and the teardown that follows would dispose the connection out from under a live
    /// reader. Esc cancels the statement; then this becomes available.
    /// </para>
    /// </summary>
    public bool CanCommit => Transaction is { State: TransactionState.Active } && Selected?.IsRunning != true;

    /// <summary>Whether the transaction can be rolled back right now. Unlike <see cref="CanCommit"/> an
    /// aborted one still can — that is the way out of it — but a tab mid-statement still cannot, for the
    /// connection reason above.</summary>
    public bool CanRollback => Transaction is not null && Selected?.IsRunning != true;

    /// <summary>
    /// The chip: how much is uncommitted and for how long — "3 uncommitted · 4m". The count is statements
    /// run in the transaction, not rows: rows would need a total nobody asked the server for.
    /// <para>
    /// An <b>aborted</b> transaction says so instead of counting. Nothing in it is waiting to be committed —
    /// it cannot be — so "2 uncommitted" would describe work the user still has a choice about, which is
    /// exactly what they no longer have.
    /// </para>
    /// </summary>
    public string TransactionText => Transaction is { } t
        ? t.State == TransactionState.Aborted
            ? $"transaction aborted · {CommitPolicy.AgeLabel(t.Age)}"
            : $"{t.StatementCount} uncommitted · {CommitPolicy.AgeLabel(t.Age)}"
        : "";

    /// <summary>Whether the chip should read as a warning: idle past the nudge threshold, or aborted.
    /// Two different things said one way on purpose — both mean "this needs you now".</summary>
    public bool TransactionIsStale => Transaction is { } t
        && (t.State == TransactionState.Aborted
            || CommitPolicy.IsStale(t.Idle, _ctx.Settings.IdleTransactionWarnMinutes));

    /// <summary>The chip's tooltip: which connection and database is being held, and why it is amber.</summary>
    public string TransactionTip => Transaction is { } t
        ? t.State == TransactionState.Aborted
            ? $"A statement failed in this transaction on {t.ConnectionName}/{t.Database} — "
              + "it can only be rolled back."
            : $"Uncommitted on {t.ConnectionName}/{t.Database}, idle {CommitPolicy.AgeLabel(t.Idle)}. "
              + "An open transaction holds locks on the server until you commit or roll it back."
        : "";

    // True while SweepTransactionsAsync is in flight. The timer is not awaited (it cannot be), so without
    // this a rollback slower than one tick lets the next tick see the same entry — logging a second
    // `rollback` row and raising a second toast for one transaction.
    private bool _sweeping;

    private void OnTransactionsChanged()
    {
        RefreshTransaction();
        // Start the clocks with the first transaction and stop them with the last, the way the elapsed
        // timer follows IsBusy.
        _transactionTimer.IsEnabled = _ctx.Transactions.Any;
    }

    /// <summary>
    /// The idle sweep for open transactions: roll back the abandoned ones, then re-render the rest (#131).
    /// <para>
    /// <b>The rollback is not a warning the user missed.</b> An open transaction holds locks on the server
    /// for as long as it lives, and a warning nobody is there to read puts no bound on that at all — so the
    /// second clock ends it. It is reported through the same completion toast a background run uses, because
    /// the user is by definition not watching the status bar, and a status line is gone by the time they
    /// come back.
    /// </para>
    /// <para>
    /// The rollback threshold is raised to the warning's when it is set below it: warning about something
    /// that has already been rolled back is a sentence about nothing, and a settings file is hand-editable.
    /// </para>
    /// </summary>
    /// <remarks>Public for the same reason <c>ConnectionSessionManager.SweepIdleAsync</c> is: the thing
    /// under test is measured in minutes, and a test that waited for the timer would be a test that waited
    /// for fifteen minutes.</remarks>
    public async Task SweepTransactionsAsync()
    {
        if (_sweeping) return;
        _sweeping = true;
        try { await SweepOnceAsync().ConfigureAwait(true); }
        finally { _sweeping = false; }
    }

    private async Task SweepOnceAsync()
    {
        var settings = _ctx.Settings;
        var rollbackAfter = settings.IdleTransactionRollbackMinutes <= 0
            ? 0
            : Math.Max(settings.IdleTransactionRollbackMinutes, settings.IdleTransactionWarnMinutes);

        foreach (var (tab, open) in _ctx.Transactions.Entries)
        {
            // A tab mid-statement is not idle, whatever the clock says, and rolling back under a live
            // command is the failure EndTransactionAsync refuses by hand. The clock is stamped when a
            // statement finishes as well as when it starts (see TrackedExecutor), so a long UPDATE no
            // longer ages past the threshold while it runs — this is the backstop for the moment between.
            if (tab.IsRunning) continue;
            if (!CommitPolicy.IsAbandoned(open.Idle, rollbackAfter)) continue;
            var idle = CommitPolicy.AgeLabel(open.Idle);
            var statements = open.StatementCount;
            await _ctx.Transactions.RollbackAsync(tab, CancellationToken.None);
            var message = $"Rolled back {statements} uncommitted statement(s) on "
                          + $"{open.ConnectionName}/{open.Database} after {idle} idle.";
            _ctx.SetStatus(message);
            // Always a toast, even when the tab is the one on screen: the whole premise of this path is
            // that nobody was looking, and RunFinished would put it in the status bar and let the next
            // thing overwrite it.
            BackgroundCompleted?.Invoke(new BackgroundCompletion(
                tab.Header, message, _ctx.AllTabs.Contains(tab), tab));
        }

        // Everything still open: the age moved, and so may the amber.
        RefreshTransaction();
    }

    /// <summary>Re-raise everything the chip and the two commands read. Cheap and coarse: there are at most
    /// a handful of transactions, and this fires on open, end, and each idle tick.</summary>
    public void RefreshTransaction()
    {
        OnPropertyChanged(nameof(Transaction));
        OnPropertyChanged(nameof(HasOpenTransaction));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(TransactionText));
        OnPropertyChanged(nameof(TransactionIsStale));
        OnPropertyChanged(nameof(TransactionTip));
    }

    // ---- the mode itself, flippable without opening the dialog (#131) -------------------------

    private ConnectionInfo? SelectedConnection => Selected is { } tab ? _ctx.EffectiveConnection(tab) : null;

    /// <summary>Whether there is a connection whose mode there is any point showing.</summary>
    public bool CommitModeVisible => SelectedConnection is not null;

    /// <summary>Whether the selected tab's connection is holding writes right now.</summary>
    public bool IsManualCommit => SelectedConnection is { } info && CommitPolicy.IsManualCommit(info);

    /// <summary>
    /// The pill: short, because it sits in a toolbar — the tooltip carries the rest. A dot marks a mode
    /// that is not the one saved on the connection, the same mark the tab strip uses for a buffer that
    /// differs from its file, and for the same reason: something here will not survive being reopened.
    /// </summary>
    public string CommitModeLabel
        => (IsManualCommit ? "Manual" : "Auto") + (CommitModeIsOverridden ? " •" : "");

    /// <summary>Whether the mode in force is not the one saved on the connection — the pill marks it, so a
    /// setting that disagrees with the dialog is never something you have to discover.</summary>
    public bool CommitModeIsOverridden
        => SelectedConnection is { } info && _ctx.CommitModes.IsOverridden(info.Id);

    public string CommitModeTip
    {
        get
        {
            if (SelectedConnection is not { } info) return "";
            var mode = IsManualCommit
                ? "Manual commit — a write opens a transaction and waits for Commit or Rollback."
                : "Auto-commit — every statement commits itself.";
            // The *saved* record, not the effective one this property reads from: what the tooltip has to
            // name is what the connection dialog would show, which is exactly what the override differs from.
            var overridden = _ctx.CommitModes.IsOverridden(info.Id) && _ctx.FindConnection(info.Id) is { } disk
                ? $"\nThis session only; {disk.Name} is saved as "
                  + (disk.ManualCommit ? "manual commit." : "auto-commit.")
                : "";
            return mode + overridden + "\nClick to switch.";
        }
    }

    /// <summary>
    /// Flip the selected tab's connection between auto and manual commit for the rest of the session
    /// (<c>transaction.toggleMode</c>). The saved connection is untouched — see <see cref="CommitModes"/>.
    /// <para>
    /// Switching <b>to auto-commit is refused</b> while any tab on that connection holds a transaction. The
    /// mode is per connection and the transactions are per tab, so there is no coherent "and this one keeps
    /// waiting"; and a toolbar pill may not be a thing that discards data or commits to production, which
    /// are the only other two answers. Refusing costs one gesture and cannot lose anything.
    /// </para>
    /// </summary>
    public void ToggleCommitMode()
    {
        if (SelectedConnection is not { } info) { _ctx.SetStatus("This tab has no connection."); return; }
        var manual = CommitPolicy.IsManualCommit(info);

        if (manual && _ctx.Transactions.All.FirstOrDefault(t => t.Key.ConnectionId == info.Id) is { } open)
        {
            _ctx.SetStatus(
                $"{open.ConnectionName}/{open.Database} has an uncommitted transaction — commit or roll it "
                + "back before switching to auto-commit.");
            return;
        }

        _ctx.CommitModes.Set(info.Id, !manual);
        RefreshCommitMode();
        _ctx.SetStatus(manual
            ? $"{info.Name}: auto-commit — statements commit themselves."
            : $"{info.Name}: manual commit — a write now waits for Commit or Rollback.");
    }

    /// <summary>Re-raise the pill's bindings. Called on a toggle, a selection change, and whenever the
    /// override map moves under us (saving the connection clears it).</summary>
    public void RefreshCommitMode()
    {
        OnPropertyChanged(nameof(CommitModeVisible));
        OnPropertyChanged(nameof(IsManualCommit));
        OnPropertyChanged(nameof(CommitModeIsOverridden));
        OnPropertyChanged(nameof(CommitModeLabel));
        OnPropertyChanged(nameof(CommitModeIsOverridden));
        OnPropertyChanged(nameof(CommitModeTip));
    }

    /// <summary>Commit the selected tab's transaction (<c>transaction.commit</c>).</summary>
    public Task CommitTransactionAsync() => EndTransactionAsync(commit: true);

    /// <summary>Roll back the selected tab's transaction (<c>transaction.rollback</c>).</summary>
    public Task RollbackTransactionAsync() => EndTransactionAsync(commit: false);

    // True while a commit or rollback is in flight. Neither verb sets tab.IsRunning and the transaction
    // stays in the map until the end completes, so without this a second click during a slow commit
    // re-enters, calls CommitAsync on a transaction already committing, and reports "Commit failed" for
    // one that in fact committed.
    private bool _ending;

    private async Task EndTransactionAsync(bool commit)
    {
        if (_ending) return;
        if (Selected is not { } tab) return;
        if (_ctx.Transactions.For(tab) is null) { _ctx.SetStatus("No open transaction on this tab."); return; }

        // Deliberately not under RunExclusiveAsync — ending a transaction is not a query on the tab — but
        // equally it may not run *beside* one. The transaction and the statement share a physical
        // connection, so a commit issued mid-statement throws from the driver and the teardown behind it
        // would dispose that connection under a live reader. Esc first, then this.
        if (tab.IsRunning)
        {
            _ctx.SetStatus(
                $"A statement is still running on {tab.Header} — cancel it with Esc, then "
                + (commit ? "commit." : "roll back."));
            return;
        }

        _ending = true;
        try
        {
            _ctx.SetStatus(commit
                ? await _ctx.Transactions.CommitAsync(tab, CancellationToken.None)
                : await _ctx.Transactions.RollbackAsync(tab, CancellationToken.None));
        }
        finally { _ending = false; }
        RefreshTransaction();
    }


    /// <summary>
    /// Which executor <paramref name="tab"/>'s statements run on: its own held transaction's when it has
    /// one, else the session's pooled one (#131). Every call site that runs the user's SQL goes through
    /// here, or it would open a fresh connection and miss the transaction entirely.
    /// <para>
    /// The key is re-checked rather than trusted. A tab may not change connection or database while a
    /// transaction is open — the guards refuse it — and if one ever slipped through, running on a
    /// connection to a different database would be worse than not being in the transaction.
    /// </para>
    /// </summary>
    private IQueryExecutor ExecutorFor(EditorTabViewModel tab, ConnectionSession session)
        => _ctx.Transactions.For(tab) is { } open && open.Key == session.Key
            ? open.Scope.Executor
            : session.Executor;

    /// <summary>Live elapsed time of the selected tab's in-flight run ("247 ms" / "1.2 s"), or empty when
    /// idle. Shown in the status bar (visibility bound to <see cref="IsBusy"/>) and refreshed ~10×/second.</summary>
    [ObservableProperty] private string _elapsedText = "";

    private EditorTabViewModel? Selected => _ctx.SelectedTab;

    /// <summary>Raised when a run finishes on a tab that is not the one on screen — a background tab, or a
    /// tab closed / left behind by a project switch while its query was still going. The view turns this
    /// into a toast; nothing else in the app is a notification sink. May be raised off the UI thread.</summary>
    public event Action<BackgroundCompletion>? BackgroundCompleted;

    /// <summary>Progress text from a run on <paramref name="tab"/>. Reaches the status bar only while that
    /// tab is the selected one: with tabs running concurrently, a background run's "Running…" would
    /// otherwise overwrite what the user is actually looking at.</summary>
    private void RunStatus(EditorTabViewModel tab, string text)
    {
        if (ReferenceEquals(_ctx.SelectedTab, tab)) _ctx.SetStatus(text);
    }

    /// <summary>A run's terminal message (summary, error, or cancellation): the status bar when its tab is
    /// on screen, a completion toast when it isn't. Every path that ends a run goes through here, so a
    /// background query can't finish silently.</summary>
    private void RunFinished(EditorTabViewModel tab, string text)
    {
        if (ReferenceEquals(_ctx.SelectedTab, tab)) { _ctx.SetStatus(text); return; }
        // "Still open" spans every open project, not just the visible one: a tab parked by a project switch
        // is alive and holding its results, so the toast must offer to go back to it rather than claim the
        // run was thrown away.
        var stillOpen = _ctx.AllTabs.Contains(tab);
        BackgroundCompleted?.Invoke(new BackgroundCompletion(tab.Header, text, stillOpen, stillOpen ? tab : null));
    }

    /// <summary>Whether the *selected* tab has a query in flight — drives the toolbar Run/Cancel button
    /// and Esc. Background tabs run concurrently and independently; this reflects only the focused tab.</summary>
    public bool IsBusy => Selected?.IsRunning ?? false;

    /// <summary>The Run button doubles as Cancel while the selected tab's query is in flight.</summary>
    public string RunButtonText => IsBusy ? "Cancel (Esc)" : "Run (Ctrl+Enter)";

    // Re-raise the façade properties when the selection changes, and re-subscribe to the newly selected
    // tab so its IsRunning transitions (start/finish/cancel) update the toolbar button live.
    private void OnSelectedTabChanged()
    {
        if (_watchedTab is not null) _watchedTab.PropertyChanged -= OnWatchedTabChanged;
        _watchedTab = Selected;
        if (_watchedTab is not null) _watchedTab.PropertyChanged += OnWatchedTabChanged;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(RunButtonText));
        RefreshTransaction();
        RefreshCommitMode();   // a different tab can be on a different connection, and so a different mode
        SyncElapsedTimer();
    }

    private void OnWatchedTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-pointing the tab changes which connection's mode the pill is describing.
        if (e.PropertyName is nameof(EditorTabViewModel.ConnectionId) or nameof(EditorTabViewModel.DatabaseName))
            RefreshCommitMode();
        if (e.PropertyName == nameof(EditorTabViewModel.IsRunning))
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(RunButtonText));
            // Both verbs are unavailable mid-statement (see CanCommit), so they move with the run.
            RefreshTransaction();
            SyncElapsedTimer();
        }
    }

    // Run the timer exactly when the selected tab is in flight. Reading RunElapsed off the current
    // selection means switching tabs mid-run picks up the newly-focused tab's clock automatically.
    private void SyncElapsedTimer()
    {
        if (IsBusy)
        {
            RefreshElapsed();       // show a value immediately rather than after the first 100 ms tick
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            ElapsedText = "";
        }
    }

    private void RefreshElapsed()
    {
        if (Selected is { IsRunning: true } tab)
            ElapsedText = ResultSetBuilder.FormatElapsed(tab.RunElapsed.TotalMilliseconds);
    }

    /// <summary>Execute SQL for the selected tab against that tab's connection; record it in the log.</summary>
    public async Task ExecuteAsync(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        var tab = Selected;
        if (tab is null) { _ctx.SetStatus("No editor."); return; }
        if (tab.IsRunning) return; // this tab already has an operation in flight (one per tab)
        if (tab.ConnectionId is null) { _ctx.SetStatus("This tab has no connection — pick one."); return; }
        var info = _ctx.EffectiveConnection(tab);
        if (info is null) { _ctx.SetStatus("Connection no longer exists."); return; }
        // Everything text-shaped below — the write guard, the first-page limit, the page suffix — is the
        // selected connection's, resolved here rather than defaulted: a tab on SQL Server paged with
        // Postgres' `limit/offset` is the bug this resolution exists to make impossible.
        var traits = ProviderTraits.For(info);

        // Describe (not FindRiskyStatements) so the prompt below can list the statements it is about to run —
        // the verbs alone can't answer "what exactly lands on prod". Computed once and shared with both the
        // refusal and the confirmation, which ask different questions of the same verdict — but only when one
        // of them can act on it: lexing every batch on a connection that neither refuses nor confirms would
        // tax every Run in the app for two settings it does not have.
        // The connection's dialect, not a Postgres default: the guard's verdict depends on whether it can
        // read this engine at all, and an engine it cannot read has every statement treated as risky with a
        // label saying why (§1.2 — never narrower for any dialect).
        var risks = info.ReadOnly || info.RequireWriteConfirmation || CommitPolicy.IsManualCommit(info)
            ? WriteGuard.Describe(traits.Dialect, sql)
            : [];

        // Manual-commit: Bearing is holding the transaction on this connection, so a typed BEGIN / COMMIT /
        // ROLLBACK is refused rather than run behind the driver's back (#131). Ahead of the read-only
        // refusal because it is about who owns the transaction rather than about what the statement writes,
        // and a COMMIT is neither a read nor a write.
        if (WriteRefusal.ReasonForTransactionControl(info, risks) is { } notOurs)
        { _ctx.SetStatus(notOurs); return; }

        // Read-only connection: refuse, don't ask (#99). Ahead of the confirmation because it is independent
        // of RequireWriteConfirmation — a read-only connection refuses whether or not it also prompts — and
        // because there is nothing to confirm when the answer is already no. The server would refuse this
        // anyway; getting there first is what turns a 25006 into a sentence naming the user's own setting.
        if (WriteRefusal.Reason(info, risks) is { } refused) { _ctx.SetStatus(refused); return; }

        // Production write-guard: confirm before writing data / altering schema on a guarded connection.
        if (info.RequireWriteConfirmation && _dialogs is { } dialogs)
        {
            if (risks.Any(s => s.IsRisky))
            {
                var impacts = await CountRowImpactsAsync(risks);
                if (!await dialogs.ConfirmWriteAsync(WriteConfirmation.ForBatch(info, risks, impacts)))
                {
                    _ctx.SetStatus("Cancelled — write not confirmed.");
                    return;
                }
            }
        }

        // AutosaveMode.OnExecute writes the buffer at the moment it runs, so what's on disk is what was
        // executed. Before the run, not after: a query that errors or is cancelled still reflects the text
        // that produced it. No-op in the other modes.
        if (_ctx.Autosave is { } autosave) await autosave.OnExecutedAsync(tab);

        // Manual-commit opens on the first *write*, never on a read: a transaction left open by nothing but
        // browsing a table is the commonest way to end up holding locks on a production server (#131). Once
        // one is open every statement joins it, reads included — that is what makes the row-count preview
        // and the grid agree with what the transaction has actually done.
        // Decided here and acted on inside the run, because opening one needs a live session and the run is
        // what connects.
        // NamesAWrite, not IsRisky. The guard's verdict is deliberately generous — on T-SQL anything whose
        // lead word is not a known read is treated as a write so that it gets confirmed — and that is the
        // wrong test for this question: `declare @id int; select …` is ordinary T-SQL for a read, and
        // opening a transaction on it would make browsing hold locks.
        var opensTransaction = CommitPolicy.IsManualCommit(info)
            && _ctx.Transactions.For(tab) is null
            && risks.Any(s => s.NamesAWrite);

        await RunExclusiveAsync(tab, async ct =>
        {
            // Push a server-side LIMIT for a single read-only SELECT so a remote server produces only
            // ~one page instead of streaming the whole result. Fetch one extra row (PageSize+1) so the
            // executor's Truncated flag still signals "more rows exist". Writes / multi-statement /
            // already-limited queries return null here and run unbounded (capped client-side). The set
            // still pages/counts against the original sql, so SourceSql is unchanged.
            var fetchSql = FirstPageLimiter.TryAppendLimit(traits.Dialect, sql, PageSize + 1) ?? sql;

            // Prompt / Entra credentials can go stale (expired token, or a wrong password typed once): run,
            // and on an auth failure refresh the credential and retry exactly once. A stored password that
            // didn't change can't be helped by a retry, so it surfaces the error on the first pass.
            var canRefresh = CanRefreshCredential(info);
            var outcome = await RunFetchAsync(info, tab, sql, fetchSql, opensTransaction, final: !canRefresh, ct);
            if (outcome == RunOutcome.AuthFailed && canRefresh && !ct.IsCancellationRequested)
            {
                _ctx.Credentials.Invalidate(info.Id);
                await _ctx.Sessions.EvictAsync(SessionKey.For(info));
                RunStatus(tab, "Reauthenticating…");
                await RunFetchAsync(info, tab, sql, fetchSql, opensTransaction, final: true, ct);
            }
        }, "Query cancelled by user.", "Execution error");
    }

    private enum RunOutcome { Success, AuthFailed, Failed }

    /// <summary>Per-count deadline (#112). Short on purpose: the dialog is waiting on it.</summary>
    private static readonly TimeSpan CountTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Deadline for all of a batch's counts together, so a script full of writes cannot add up to a
    /// long pause before the prompt appears. Statements past it report as uncounted, which is honest.</summary>
    private static readonly TimeSpan CountBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How many rows each write in the batch will touch (#112), keyed by statement index — the number that
    /// turns the confirmation from a ritual into information.
    /// <para>
    /// Three deliberate refusals. It never <b>connects</b>: a count is worth less than the credential prompt
    /// that a connect could raise ahead of a confirmation the user has not answered yet, so an unconnected
    /// tab simply shows no numbers. It never <b>blocks</b>: each count has a deadline and the batch has a
    /// smaller total one, and anything that misses reports as uncounted rather than holding the dialog. And
    /// it never <b>guesses</b>: <see cref="UpdateDeleteTarget.TryReduce"/> declines every statement it cannot
    /// flatten with certainty, so a statement shows no number rather than a wrong one.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<int, RowImpact>?> CountRowImpactsAsync(
        IReadOnlyList<StatementRisk> statements)
    {
        var impacts = new Dictionary<int, RowImpact>();
        var toCount = new List<(int Index, UpdateDeleteTarget Target)>();

        for (var i = 0; i < statements.Count; i++)
        {
            if (!statements[i].IsRisky) continue;
            if (UpdateDeleteTarget.TryReduce(statements[i].Text) is not { } target) continue;
            // "Every row" is answered by the statement itself — #100's warning needs no server round trip,
            // and asking one for it would be the slowest way to learn what the missing WHERE already said.
            if (target.EveryRow) impacts[i] = RowImpact.Every(target);
            else toCount.Add((i, target));
        }

        if (toCount.Count == 0) return impacts.Count > 0 ? impacts : null;

        if (Selected is not { } tab || _ctx.EffectiveConnection(tab) is not { } info
            || _ctx.Sessions.TryGet(SessionKey.For(info)) is not { } session)
            return impacts.Count > 0 ? impacts : null;

        // Leased for the counts only, and released before the dialog opens: a modal prompt must not hold a
        // session open while the user reads it (the same rule SaveChangesAsync follows).
        using var lease = _ctx.Sessions.Lease(session);
        using var budget = new CancellationTokenSource(CountBudget);
        // On the tab's own transaction when it has one: inside an open transaction the count that matters is
        // the one that sees its uncommitted rows, which is the answer the user is about to act on (#131).
        var executor = ExecutorFor(tab, session);
        foreach (var (index, target) in toCount)
            impacts[index] = await CountOneAsync(session, executor, target, budget.Token);

        return impacts;
    }

    /// <summary>
    /// One statement's count, or <see cref="RowImpact.Uncounted"/>. Every failure collapses to the same
    /// answer on purpose: a timeout, a cancelled command, a permission denial and a relation that has since
    /// been dropped all leave the user with no number, and none of them is a reason to fail — or delay — a
    /// confirmation the user asked for.
    /// </summary>
    private static async Task<RowImpact> CountOneAsync(
        ConnectionSession session, IQueryExecutor executor, UpdateDeleteTarget target, CancellationToken budget)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget);
            deadline.CancelAfter(CountTimeout);
            // The wrapper is the connection's, shaped here for the paging count's reason: CountAsync runs
            // the text it is handed and does not wrap it, so passing RowsSql bare made ExecuteScalar read
            // the first column of the first row — "this will delete 8 rows" for a statement that deletes 3.
            // A dialect that refuses the shape leaves the confirmation with no number, which is #1.5's
            // "never guesses" arm rather than a failure.
            if (ProviderTraits.For(session.Info).Dialect.CountWrap(target.RowsSql) is not { } countSql)
                return RowImpact.Uncounted(target);
            var rows = await executor.CountAsync(countSql, deadline.Token);
            return rows is { } n ? RowImpact.Counted(target, n) : RowImpact.Uncounted(target);
        }
        catch (Exception)
        {
            return RowImpact.Uncounted(target);
        }
    }

    /// <summary>Acquire a lease, run the first-page fetch, and — unless this is a non-final attempt that hit
    /// an authentication failure — bind + log the result. Returns <see cref="RunOutcome.AuthFailed"/> without
    /// binding/logging on a non-final attempt so the caller can refresh the credential and retry once; on the
    /// final attempt any error (auth included) is surfaced normally.</summary>
    private async Task<RunOutcome> RunFetchAsync(
        ConnectionInfo info, EditorTabViewModel tab, string sql, string fetchSql, bool opensTransaction,
        bool final, CancellationToken ct)
    {
        var wall = Stopwatch.StartNew();
        // Acquire a lease so an idle sweep / evict / database switch can't dispose the pool from
        // under this query while it runs (the lease is held for the whole read).
        SessionLease lease;
        try { lease = await _ctx.Sessions.AcquireAsync(info, ct); }
        catch (ConnectionFailedException ex)
        {
            if (!final && Classify(info, ex) == DbErrorKind.Authentication) return RunOutcome.AuthFailed;
            _ctx.IsConnected = false;
            RunFinished(tab, ex.Message);
            return RunOutcome.Failed;
        }

        using (lease)
        {
            var session = lease.Session;
            _ctx.IsConnected = true;
            // Warm the schema in parallel with the fetch — editability (below) needs the snapshot, and
            // with no connect-on-tab-switch anymore this run is the first thing to load it.
            var schemaWarm = _ctx.Sessions.EnsureSchemaAsync(session, CancellationToken.None);

            // Opened here rather than before the run: it needs a live session, and this is the call that
            // connects. Before the statement, so the write it was opened for lands inside it.
            if (opensTransaction && _ctx.Transactions.For(tab) is null)
            {
                var (_, error) = await _ctx.Transactions.OpenAsync(tab, info, session, ct);
                if (error is not null) { RunFinished(tab, error); return RunOutcome.Failed; }
            }

            RunStatus(tab, "Running…");
            var results = await ExecutorFor(tab, session)
                .ExecuteAsync(fetchSql, new QueryOptions { MaxRows = PageSize }, ct);
            wall.Stop();

            // Auth rejected on open (stale token) comes back as a QueryError; retry before binding/logging.
            if (!final && !ct.IsCancellationRequested
                && results.Any(r => r.Error is { } e && Classify(info, e) == DbErrorKind.Authentication))
                return RunOutcome.AuthFailed;

            // Ensure the snapshot is loaded so a first-page result resolves as editable. Only the first
            // run per session waits (the snapshot is cached thereafter); best-effort so a schema-load
            // failure still shows the rows, just non-editable.
            if (session.Snapshot is null) { try { await schemaWarm; } catch { /* results still show */ } }
            // Results always route to the tab that started the run, never to whichever tab is focused now.
            // A tab that has since been closed (or dropped by a project switch) is dead but harmless to
            // assign to — RunFinished is what tells the user the run ended, with TabStillOpen false.
            tab.SetFreshResults(ResultSetBuilder.BuildResultSets(
                results, sql, session.Snapshot, ProviderTraits.For(info), SessionPolicy.IsReadOnly(info)));
            LogExecution(info, sql, results, _ctx.Transactions.For(tab)?.Id);
            var summary = ResultSetBuilder.DescribeResults(
                results, wall.Elapsed, info, ct.IsCancellationRequested);
            // On success, lead with the connection so the status bar reads e.g. "pagila (local) · 88 ms".
            RunFinished(tab, results.Any(r => !r.Success) ? summary : $"{info.Name} · {summary}");
            return results.Any(r => !r.Success) ? RunOutcome.Failed : RunOutcome.Success;
        }
    }

    /// <summary>Which credential kinds a fresh acquire can actually help. A kind that resolves to the same
    /// value twice must not be retried: the second attempt fails identically, and the user reads
    /// "Reauthenticating…" for an authentication that never happened. One arm per kind, with its reason,
    /// because the arms are not obvious from the outside.</summary>
    private bool CanRefreshCredential(ConnectionInfo info) => info.CredentialKind switch
    {
        // Re-asking the user, and re-minting a token, are both things a retry can actually change.
        CredentialKind.Prompt or CredentialKind.EntraToken => true,

        // A stored password that didn't change would just fail identically — unless the store can't hold
        // passwords at all (no keyring), where "stored password" really means "there is nothing stored" and
        // the retry is what gets the user prompted for one.
        CredentialKind.StoredPassword => _ctx.Secrets is { CanStore: false },

        // The OS identity: nothing to re-prompt for and nothing to re-mint, so a retry would send the exact
        // same handshake. Spelled out rather than left to the default arm because the case it must not fall
        // into is right above it — an Integrated connection swept into the no-keyring branch would
        // "reauthenticate" by doing nothing, twice.
        CredentialKind.Integrated => false,

        _ => false,
    };

    /// <summary>
    /// The engine's own verdict on a failed statement. The App layer used to read Postgres SQLSTATEs as
    /// strings (a <c>28</c> prefix meant "auth"), which silently mislabelled every other engine's codes —
    /// SQL Server has no SQLSTATEs at all. <see cref="IDbProvider.Classify"/> is where that knowledge lives
    /// now, and the Postgres verdicts are unchanged.
    /// </summary>
    private DbErrorKind Classify(ConnectionInfo info, QueryError error)
        => ProviderFor(info) is { } provider ? provider.Classify(error) : DbErrorKind.Unknown;

    /// <summary>The same judgement for a thrown failure — what the connect path has, since a failed
    /// handshake never produces a <see cref="QueryError"/>.</summary>
    private DbErrorKind Classify(ConnectionInfo info, Exception exception)
        => ProviderFor(info) is { } provider ? provider.ClassifyException(exception) : DbErrorKind.Unknown;

    /// <summary>The provider for a connection, or null when this build no longer ships that engine. Null
    /// rather than a throw: classification only ever decides how a failure is <em>reported</em>, so an
    /// unresolvable provider must degrade to <see cref="DbErrorKind.Unknown"/> — showing the driver's
    /// message as-is — not turn a reportable error into a second one.</summary>
    private IDbProvider? ProviderFor(ConnectionInfo info)
    {
        try { return _ctx.Providers.Get(info.ProviderId); }
        catch (KeyNotFoundException) { return null; }
    }

    /// <summary>Run <paramref name="body"/> under <paramref name="tab"/>'s per-tab single-flight lifecycle:
    /// raise that tab's busy flag + a fresh cancellation source (so <see cref="CancelExecution"/> can cancel
    /// it while the tab is selected), pass its token to the body, and always tear the run down afterwards.
    /// Cancellation and errors become a status line ("<c>{cancelled}</c>" / "<c>{failed}: {message}</c>").
    /// Callers pre-check <c>tab.IsRunning</c>; this assumes the tab isn't already running.</summary>
    private async Task RunExclusiveAsync(
        EditorTabViewModel tab, Func<CancellationToken, Task> body, string cancelled, string failed)
    {
        var ct = tab.BeginRun();
        try { await body(ct); }
        // Cancellation only ever comes from the user (Esc, the Run/Cancel button, closing the tab, quitting),
        // so it reports through RunStatus, not RunFinished: telling someone their query stopped right after
        // they asked it to stop is noise, and it would toast on exactly the paths where the tab is going away.
        catch (OperationCanceledException) { RunStatus(tab, cancelled); }
        // A cancelled statement surfaces as the driver's own exception, not an OperationCanceledException
        // (Npgsql raises SQLSTATE 57014; SqlClient raises error 0) — so any failure while our own token is
        // cancelled is treated as the user's cancel. The token, not the code, is what settles it here, which
        // is why this needs no classifier: whatever the engine called it, we asked for it.
        catch (Exception) when (ct.IsCancellationRequested) { RunStatus(tab, cancelled); }
        catch (Exception ex) { RunFinished(tab, $"{failed}: {ex.Message}"); }
        finally { tab.EndRun(); }
    }

    /// <summary>Cancel the selected tab's in-flight query, if any (Esc / the Run button while busy).
    /// Only the focused tab is cancelled — a background tab's query keeps running.</summary>
    public void CancelExecution() => Selected?.CancelRun();

    /// <summary>Append the next page to a pageable result set (infinite-scroll "load more").</summary>
    public async Task LoadMoreAsync(ResultSetViewModel rs)
    {
        var tab = Selected;
        if (tab is null || tab.IsRunning || !rs.IsPageable || rs.SourceSql is null || !rs.HasMore) return;
        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        await RunExclusiveAsync(tab, async ct =>
        {
            // PageSql shapes the paging: a top-level limit/offset (same as the first page, so the query's
            // ORDER BY is honored consistently across pages) when the query allows a safe suffix, else a
            // subquery wrap. The executor just runs the string.
            if (PageSql.Page(ProviderTraits.For(session.Info).Dialect, rs.SourceSql, rs.Loaded, PageSize)
                is not { } pageSql)
            {
                // The dialect refuses both shapes, so this query cannot be paged on this engine at all —
                // a CTE on SQL Server, which may not sit in a derived table. Say so once and retire the
                // affordances; the alternative was sending SQL the server rejects on every scroll.
                rs.RetirePaging();
                RunStatus(tab, "Paging unavailable for this query on this engine — the rows already loaded are all of them shown.");
                return;
            }
            var page = await ExecutorFor(tab, session).ExecutePageAsync(pageSql, ct);

            // A failed page arrives as an error result, not a throw, so it has to be checked for. Appending
            // it would append nothing and clear HasMore: the result would silently look complete — auto-load
            // and [⤓ all] would retire, and an export taken afterwards would write the rows that happened to
            // be on screen and report success. Leaving rows and HasMore untouched also means the next scroll
            // simply retries.
            if (!page.Success)
            {
                // The user's own Esc is answered by our token, and only by our token: it is the one cancel
                // this app asked for, so it is the one that reports as a cancel.
                ct.ThrowIfCancellationRequested();
                // Anything else the driver calls a cancel was NOT requested here — Postgres raises 57014
                // for `statement_timeout` and `pg_cancel_backend` as well as for Esc, and SqlClient's
                // error 0 covers several client-side faults. Classifying those as "Load cancelled."
                // discarded the server's own message and left the next scroll silently retrying the same
                // doomed page, so the message is kept and only the wording softens.
                if (page.Error is { } stopped
                    && Classify(session.Info, stopped) == DbErrorKind.Canceled)
                { RunStatus(tab, $"Load stopped: {stopped.Message}"); return; }
                RunFinished(tab, $"Load more failed: {page.Error?.Message}");
                return;
            }

            rs.AppendPage(page.Rows, page.RowCount == PageSize);
            // No status update on success: auto-load fires on scroll and the count lives on the meta row.
        }, "Load cancelled.", "Load more failed");
    }

    /// <summary>
    /// Load a result set to the end in one action (the ⤓ all button) instead of scrolling through it.
    /// <para>
    /// <b>One execution, streamed</b> — not a walk over pages. Walking meant re-running the query per page
    /// with a growing OFFSET (quadratic server work), and, because each page was its own statement, a
    /// concurrent insert or delete could shift rows between pages so the fetch silently duplicated or skipped
    /// some and still reported a complete result. One statement is one snapshot.
    /// </para>
    /// Cancelable like any run (Esc / the Run button), reports progress per batch as the reader drains, and
    /// stops at <see cref="AppSettings.ResultFetchAllMaxRows"/> so a mistyped query can't read until the app
    /// dies. Rows already materialized are kept on cancel.
    /// </summary>
    /// <returns>True when the result is fully loaded (or was already) — false if the fetch was cancelled,
    /// failed, or stopped at the row ceiling. Callers that follow a fetch with something else (Export) use
    /// this to avoid acting on half a result.</returns>
    /// <summary>
    /// Explain the statement under the caret, and hand back the parsed plan (null when there isn't one).
    /// <para>
    /// <paramref name="analyze"/> false asks the planner and runs nothing. True <b>runs the statement</b> —
    /// that is what ANALYZE means — inside a transaction <see cref="ExplainSql.Measured"/> rolls back, so a
    /// plan can be measured for an UPDATE without the UPDATE landing.
    /// </para>
    /// <para>
    /// The write guard still applies to the measured form on a guarded connection. A rollback makes the
    /// statement harmless to the data, not harmless to the server: it takes the same locks and does the same
    /// work, and someone explaining a DELETE against production should be told they are about to run it.
    /// Plain EXPLAIN executes nothing and is never confirmed.
    /// </para>
    /// </summary>
    public async Task<ExplainPlan?> ExplainAsync(string sql, bool analyze)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        var tab = Selected;
        if (tab is null) { _ctx.SetStatus("No editor."); return null; }
        if (tab.IsRunning) return null;
        if (tab.ConnectionId is null) { _ctx.SetStatus("This tab has no connection — pick one."); return null; }
        var info = _ctx.EffectiveConnection(tab);
        if (info is null) { _ctx.SetStatus("Connection no longer exists."); return null; }

        // ExplainSql's text is Postgres' and ExplainPlanParser reads Postgres' plan JSON back, so on any
        // other engine this could only hand the user the server's own syntax error. Said before it is sent.
        if (!ProviderTraits.For(info).Dialect.SupportsExplainPlan)
        {
            _ctx.SetStatus("Query plans are not available for this engine yet.");
            return null;
        }

        var request = analyze ? ExplainSql.Measured(sql) : ExplainSql.Plan(sql);

        // EXPLAIN ANALYZE runs the statement, so a read-only connection refuses one over a write (#99) — and
        // WriteGuard already scans an EXPLAIN's interior, so `explain analyze delete …` is caught here rather
        // than by the server. A plain EXPLAIN executes nothing and a measured SELECT is a read: both still run
        // on a read-only connection, which is what #99 asks for.
        var risks = analyze && (info.ReadOnly || info.RequireWriteConfirmation)
            // The connection's dialect, as ExecuteAsync does (ExplainAsync has no `traits` local of its
            // own). This call site was the one left on the Postgres lexer, which has no notion of a
            // [bracketed] identifier — so a T-SQL batch whose write sat behind one could be read as words
            // and under-reported, and §1.2 does not allow the guard to be narrower for one dialect than
            // another.
            ? WriteGuard.Describe(ProviderTraits.For(info).Dialect, sql)
            : [];

        if (analyze && WriteRefusal.Reason(info, risks) is { } refused)
        {
            _ctx.SetStatus(refused);
            return null;
        }

        // A measured EXPLAIN cannot be run while this tab holds a transaction, and both halves of why are
        // about the same fact — a transaction lives on one connection (#131).
        //
        // It carries its own BEGIN … ROLLBACK (ExplainSql.Measured, and it needs to: a plain SELECT can call
        // a volatile function that writes). Issued on the held connection, that ROLLBACK would end the
        // *user's* transaction instead of the plan's. Issued on a pooled one — which is what it used to do —
        // it executes the statement against rows this tab's own transaction has locked, and blocks: with no
        // statement timeout by default it waits forever, and while it waits the tab counts as running, so
        // Commit and Rollback are both out of reach. The one action that would release the lock is the one
        // the hang takes away.
        //
        // A plan-only EXPLAIN has neither problem: it carries no transaction control and executes nothing,
        // so it runs on the transaction below like any other read.
        if (analyze && _ctx.Transactions.For(tab) is { } held)
        {
            _ctx.SetStatus(
                $"This tab has an uncommitted transaction on {held.ConnectionName}/{held.Database} — "
                + "EXPLAIN ANALYZE runs the statement and cannot join it. Commit or roll back first, "
                + "or use Explain, which executes nothing.");
            return null;
        }

        if (analyze && info.RequireWriteConfirmation && _dialogs is { } dialogs)
        {
            if (risks.Any(s => s.IsRisky)
                && !await dialogs.ConfirmWriteAsync(WriteConfirmation.ForBatch(info, risks)))
            {
                _ctx.SetStatus("Cancelled — EXPLAIN ANALYZE runs the statement, and it wasn't confirmed.");
                return null;
            }
        }

        using var lease = ResolveLiveLease();
        if (lease is null) return null;
        var session = lease.Session;

        ExplainPlan? plan = null;
        await RunExclusiveAsync(tab, async ct =>
        {
            // The plan-only form joins the tab's transaction like any other read, so the plan describes
            // the state the user is actually looking at. The measured form never gets here — it carries its
            // own transaction control and was refused above.
            var results = await ExecutorFor(tab, session).ExecuteAsync(request.Sql, new QueryOptions(), ct);

            // BEGIN and ROLLBACK produce no rows, so the one result that has any is the plan. An error
            // anywhere in the batch arrives as a failed result rather than a throw, and its message is worth
            // more to the user than "no plan".
            if (results.FirstOrDefault(r => r.Error is not null)?.Error is { } error)
            {
                _ctx.SetStatus($"EXPLAIN failed: {error.Message}");
                return;
            }

            var json = results.FirstOrDefault(r => r.Rows.Count > 0)?.Rows[0][0]?.ToString();
            plan = ExplainPlanParser.Parse(json, request.Analyzed, request.RolledBack);
            if (plan is null)
                _ctx.SetStatus("The server did not return a query plan.");
        },
        cancelled: "EXPLAIN cancelled.",
        failed: "EXPLAIN failed.");

        return plan;
    }

    public async Task<bool> FetchAllAsync(ResultSetViewModel rs)
    {
        var tab = Selected;
        if (tab is null || tab.IsRunning || !rs.IsPageable || rs.SourceSql is null) return false;
        if (!rs.HasMore) return true;
        using var lease = ResolveLiveLease();
        if (lease is null) return false;
        var session = lease.Session;
        var cap = Math.Max(1, _ctx.Settings.ResultFetchAllMaxRows);
        var complete = false;

        await RunExclusiveAsync(tab, async ct =>
        {
            // Room left under the ceiling. Already at it before we start (a page size configured above the
            // cap) is the same non-silent stop as running into it mid-read.
            var room = cap - rs.Loaded;
            if (room <= 0) { ReportCeiling(tab, cap); return; }

            // The rows the first page already showed are skipped with an OFFSET — they came from the earlier
            // statement and stay on screen, so scroll position and any pending edits survive the fetch.
            // Asking for room + 1 lets the reader see one row past the ceiling: that is what distinguishes
            // "the result ends here" from "we stopped early", with no extra query.
            if (PageSql.Page(ProviderTraits.For(session.Info).Dialect, rs.SourceSql, rs.Loaded, room + 1)
                is not { } sql)
            {
                // Same refusal as load-more: no shape this engine will wrap, so fetch-all has nothing to
                // fetch with. Retire rather than stream a query the server would reject. `complete` stays
                // false, so a caller chaining an Export off this still won't act on half a result.
                rs.RetirePaging();
                RunStatus(tab, "Fetch all unavailable for this query on this engine.");
                return;
            }
            var options = new QueryOptions { MaxRows = room, BatchRows = PageSize };
            var truncated = false;

            // No ConfigureAwait(false) here, deliberately: each batch is appended to Rows, which the grid is
            // bound to, so the loop body must resume on the UI thread. The reader itself drains off it — that
            // is what the Bearing.Data ConfigureAwait pass is for.
            await foreach (var batch in ExecutorFor(tab, session).StreamRowsAsync(sql, options, ct))
            {
                // hasMore stays true for the duration (the honest value while a read is in flight) and is
                // settled once below. Nothing can act on it meanwhile — the tab is running.
                rs.AppendPage(batch.Rows, hasMore: true);
                truncated = batch.Truncated;
                RunStatus(tab, $"Fetching all rows… {rs.Loaded:N0} so far (Esc to stop)");
            }

            rs.HasMore = truncated;
            if (truncated) { ReportCeiling(tab, cap); return; }

            complete = true;
            // The total is now known for certain — it's what we loaded — so [Count] retires without a query.
            rs.TotalCount = rs.Loaded;
            RunFinished(tab, $"Fetched all {rs.Loaded:N0} rows.");
        }, "Fetch all cancelled.", "Fetch all failed");

        return complete;
    }

    /// <summary>Report a fetch that stopped at the row ceiling. Deliberately not silent, and reported as a
    /// stop rather than a success: a truncated fetch that claimed to be complete would make the row count —
    /// and any export taken from it — quietly wrong.</summary>
    private void ReportCeiling(EditorTabViewModel tab, int cap)
        => RunFinished(tab, $"Stopped at {cap:N0} rows (the Fetch all limit). "
                          + "Raise it in Settings ▸ Results if you need more.");

    /// <summary>Fill in the total row count for a pageable result set (the [Count] action).</summary>
    public async Task CountTotalAsync(ResultSetViewModel rs)
    {
        var tab = Selected;
        if (tab is null || tab.IsRunning || !rs.IsPageable || rs.SourceSql is null) return;
        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        await RunExclusiveAsync(tab, async ct =>
        {
            // The wrapper is dialect-varying text, so it is shaped here — where the connection is known —
            // and the executor only runs it, exactly as it only runs a page PageSql shaped. A dialect that
            // refuses the shape (a CTE on SQL Server can't be a derived table) is answered here rather than
            // by asking the server a question it cannot parse and reading "no total" out of the error.
            if (ProviderTraits.For(session.Info).Dialect.CountWrap(rs.SourceSql) is not { } countSql)
            {
                RunFinished(tab, "Count unavailable for this query on this engine.");
                return;
            }
            // Null means the query can't be counted at all (shape), so "unavailable" is the honest report.
            // A *failed* count throws instead and lands in RunExclusiveAsync's "Count failed: …" — TotalCount
            // stays null, so CanCount stays true and the [Count] action is still there to retry.
            rs.TotalCount = await ExecutorFor(tab, session).CountAsync(countSql, ct);
            RunFinished(tab, rs.TotalCount is not null ? "Counted total." : "Count unavailable for this query.");
        }, "Count cancelled.", "Count failed");
    }

    /// <summary>Raised after a result set has been written to a file, so the view can offer to open the
    /// containing folder. Not raised for a cancelled or failed export.</summary>
    public event Action<ExportCompletion>? ExportCompleted;

    /// <summary>
    /// Export a result set to a file the user picks.
    /// <para>
    /// A paged result is <b>fetched to the end first</b>: "export" that quietly wrote the 100 rows that
    /// happened to be on screen is the kind of silent truncation that gets acted on downstream. The fetch is
    /// the same cancelable, capped one behind the ⤓ all button, and if it doesn't complete the export is
    /// abandoned rather than writing part of the answer.
    /// </para>
    /// </summary>
    public async Task ExportAsync(ResultSetViewModel rs, ExportFormat format)
    {
        var tab = Selected;
        if (tab is null) { _ctx.SetStatus("No editor."); return; }
        if (tab.IsRunning) { _ctx.SetStatus("Wait for the running query to finish before exporting."); return; }
        if (!rs.HasGrid) { _ctx.SetStatus("Nothing to export."); return; }
        if (_dialogs is not { } dialogs) return;

        if (rs.HasMore && !await FetchAllAsync(rs))
        {
            _ctx.SetStatus("Export stopped — the result isn't fully loaded.");
            return;
        }

        var suggested = ResultBlocks.SuggestedName(rs, tab.Header, DateTime.Now, format);
        if (await dialogs.PickExportFileAsync(suggested, format) is not { } path) return;

        // Snapshot the rows on this (UI) thread — Rows is an observable collection the grid keeps mutating —
        // then format and write off it: a 200k-row workbook is seconds of pure CPU, and doing that on the
        // dispatcher would freeze the window (the same reasoning as the data layer's ConfigureAwait pass).
        var block = ResultBlocks.ForResult(rs);
        var sheet = ResultBlocks.SheetName(rs);
        _ctx.SetStatus($"Exporting {block.Rows.Count:N0} rows…");
        try
        {
            await Task.Run(() => ResultExport.Write(path, block, format, sheet));
        }
        catch (Exception ex)
        {
            // Best-effort like every other write in the app (§5.2): report it, don't take the app down.
            _ctx.SetStatus($"Export failed: {ex.Message}");
            return;
        }

        _ctx.SetStatus($"Exported {block.Rows.Count:N0} rows to {System.IO.Path.GetFileName(path)}.");
        ExportCompleted?.Invoke(new ExportCompletion(path, block.Rows.Count, format));
    }

    /// <summary>
    /// Export every grid result of the current run as one workbook, a sheet each (#12).
    /// <para>
    /// <b>All or nothing.</b> Each incomplete set has to be fetched to the end first, and if any of them
    /// cannot be, nothing is written. A workbook leaves the app: it gets mailed, filed, and read by someone
    /// who was not here, and a sheet quietly missing from it is undetectable downstream. A failed export is
    /// obvious and can simply be repeated.
    /// </para>
    /// </summary>
    public async Task ExportRunAsync()
    {
        var tab = Selected;
        if (tab is null) { _ctx.SetStatus("No editor."); return; }
        if (tab.IsRunning) { _ctx.SetStatus("Wait for the running query to finish before exporting."); return; }
        if (_dialogs is not { } dialogs) return;

        var results = tab.Results.ToList();
        if (!results.Any(r => r.HasGrid))
        {
            _ctx.SetStatus(results.Count == 0 ? "Nothing to export." : "No result grids in this run to export.");
            return;
        }

        // Every set, before the file dialog: being asked where to save and *then* told the export cannot
        // happen is the wrong order to find out in.
        foreach (var rs in results.Where(r => r.HasGrid && r.HasMore))
        {
            if (await FetchAllAsync(rs)) continue;
            _ctx.SetStatus("Export stopped — a result isn't fully loaded, and a workbook missing a sheet is "
                         + "worse than no workbook.");
            return;
        }

        var suggested = ResultExport.SuggestedRunName(tab.Header, DateTime.Now);
        if (await dialogs.PickExportFileAsync(suggested, ExportFormat.Xlsx) is not { } path) return;

        // Snapshot on this (UI) thread — Rows is an observable collection the grid keeps mutating — then
        // format and write off it, as the single-set export does.
        var sheets = ResultBlocks.RunSheets(results);
        var rows = sheets.Sum(sheet => sheet.Block.Rows.Count);
        _ctx.SetStatus($"Exporting {sheets.Count} result{(sheets.Count == 1 ? "" : "s")}, {rows:N0} rows…");
        try
        {
            await Task.Run(() => ResultExport.WriteWorkbook(path, sheets));
        }
        catch (Exception ex)
        {
            _ctx.SetStatus($"Export failed: {ex.Message}");
            return;
        }

        _ctx.SetStatus($"Exported {sheets.Count} result{(sheets.Count == 1 ? "" : "s")} "
                     + $"({rows:N0} rows) to {System.IO.Path.GetFileName(path)}.");
        ExportCompleted?.Invoke(new ExportCompletion(path, rows, ExportFormat.Xlsx));
    }

    /// <summary>A lease on the already-connected session for the selected tab (paging/count/nav/save run
    /// post-execute, so the connection is live). Keeps the session from being disposed by an idle sweep /
    /// evict while the follow-up runs — dispose it when done. Null (with a status set) if the tab lost its
    /// connection. Resolved through the tab's <i>effective</i> connection, so the lease is on the pool for the
    /// database the tab actually targets rather than whichever one the connection record names.</summary>
    private SessionLease? ResolveLiveLease()
    {
        if (Selected is { } tab && _ctx.EffectiveConnection(tab) is { } info
            && _ctx.Sessions.TryGet(SessionKey.For(info)) is { } session)
            return _ctx.Sessions.Lease(session);
        _ctx.SetStatus("Not connected.");
        return null;
    }

    /// <summary>Navigate a foreign-key cell in place: run the lookup on the current tab's connection and
    /// swap the displayed result for the referenced row, stacking the previous result so Back can return.
    /// The query is never surfaced in the editor.</summary>
    public async Task NavigateForeignKeyAsync(ResultSetViewModel rs, int columnIndex, object?[] row)
    {
        if (Selected is not { } tab || tab.IsRunning) return;
        if (columnIndex < 0 || columnIndex >= row.Length) return;
        if (row[columnIndex] is null) { _ctx.SetStatus("Empty key — nothing to navigate to."); return; }
        if (SnapshotForSelectedTab() is not { } snapshot) { _ctx.SetStatus("Schema not loaded yet."); return; }
        if (ForeignKeyResolver.Resolve(snapshot, rs.Columns, columnIndex) is not { } target)
        { _ctx.SetStatus("Not a foreign key."); return; }
        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        var sql = ResultEditModel.BuildForeignKeySelect(ProviderTraits.For(session.Info), target, row);
        await RunExclusiveAsync(tab, async ct =>
        {
            RunStatus(tab, "Opening referenced row…");
            var results = await ExecutorFor(tab, session)
                .ExecuteAsync(sql, new QueryOptions { MaxRows = PageSize }, ct);
            var info = _ctx.EffectiveConnection(tab);
            tab.PushResults(ResultSetBuilder.BuildResultSets(
                results, sql, session.Snapshot, ProviderTraits.For(session.Info),
                info is not null && SessionPolicy.IsReadOnly(info)));
            // The connection has to travel here too, or a 57014 from its own statement timeout reads as
            // "nothing in Bearing asked for this" — a confident wrong answer about a limit we set (#105).
            RunFinished(tab, ResultSetBuilder.DescribeResults(
                results, null, info, ct.IsCancellationRequested));
        }, "Navigation cancelled.", "Navigation failed");
    }

    // ---- Inline editing — connection/transaction/status concerns; pure DML lives in Results/ResultEditModel.

    /// <summary>Apply a result set's pending edits/inserts/deletes in one transaction, then update the
    /// affected rows in place (no reload — paged-in rows and scroll are preserved).</summary>
    public async Task SaveChangesAsync(ResultSetViewModel rs)
    {
        var tab = Selected;
        if (tab is null || tab.IsRunning) return;
        if (rs.EditTarget is not { } target || !rs.HasPendingChanges) return;

        // The engine's quoting and its INSERT-returning clause both come from here — `[dbo].[t]` and
        // `output inserted.*` for T-SQL, `"public"."t"` and `returning *` for Postgres.
        var traits = ProviderTraits.For(_ctx.EffectiveConnection(tab));
        var changes = ResultEditModel.BuildPendingChanges(traits, rs, target);
        if (changes.Count == 0) { rs.ClearPending(); return; }

        var connection = _ctx.EffectiveConnection(tab);

        // A read-only connection refuses the save (#99). A backstop rather than the affordance: the grid does
        // not offer an edit on one at all — EditabilityResolver reports it and the lock chip says why — so
        // pending changes here mean the connection was marked read-only after they were made.
        if (connection is not null && WriteRefusal.ReasonForEdits(connection) is { } refused)
        {
            _ctx.SetStatus(refused);
            return;
        }

        // Every inline save confirms, showing the DML it is about to commit — this is the whole preview
        // flow (there is no separate [Preview SQL] step any more), so it can't be conditional on the
        // connection's write guard. A guarded connection gets the extra warning line, not the only prompt.
        // Ahead of the lease: a modal dialog must not hold a session open while the user reads it.
        if (connection is not null && _dialogs is { } dialogs
            && !await dialogs.ConfirmWriteAsync(
                   WriteConfirmation.ForEdits(connection, WriteStatements(traits, changes))))
        {
            _ctx.SetStatus("Cancelled — save not confirmed.");
            return;
        }

        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        await RunExclusiveAsync(tab, async ct =>
        {
            // An inline edit is a write, so on a manual-commit connection it opens the transaction the same
            // way a typed INSERT does — and joins one that is already open rather than committing itself.
            if (connection is not null && CommitPolicy.IsManualCommit(connection)
                && _ctx.Transactions.For(tab) is null)
            {
                var (_, error) = await _ctx.Transactions.OpenAsync(tab, connection, session, ct);
                if (error is not null) { RunFinished(tab, error); return; }
            }

            RunStatus(tab, $"Saving {changes.Count} change(s)…");
            var results = await ExecutorFor(tab, session)
                .ExecuteWriteAsync(changes.Select(c => c.Command).ToList(), ct);
            if (results.FirstOrDefault(r => !r.Success) is { } failed)
            { RunFinished(tab, $"Save failed: {failed.Error?.Message}"); return; } // rows/pending untouched

            ResultEditModel.ApplySavedChanges(rs, target, changes, results);
            RunFinished(tab, $"Saved {changes.Count} change(s).");
        }, "Save cancelled.", "Save failed");
    }

    /// <summary>Discard all pending changes in place (restore edited cells, drop new rows, un-mark deletes).</summary>
    public Task DiscardChangesAsync(ResultSetViewModel rs)
    {
        if (rs.HasPendingChanges) { rs.RevertPending(); _ctx.SetStatus("Changes discarded."); }
        return Task.CompletedTask;
    }

    /// <summary>The pending write statements for a result set, one per dirty row, values inlined and
    /// kind-tagged — what the save confirmation lists. Empty when there's nothing pending. Display only:
    /// the save itself runs the same statements parameterized.</summary>
    public IReadOnlyList<WriteStatement> PendingWriteStatements(ResultSetViewModel rs)
    {
        if (rs.EditTarget is not { } target || !rs.HasPendingChanges) return Array.Empty<WriteStatement>();
        var traits = ProviderTraits.For(Selected is { } tab ? _ctx.EffectiveConnection(tab) : null);
        return WriteStatements(traits, ResultEditModel.BuildPendingChanges(traits, rs, target));
    }

    /// <summary>
    /// The pending edits as the confirmation record that describes them (#114) — the same
    /// <see cref="WriteConfirmation.ForEdits"/> the save dialog is handed, so "show me the SQL" and "save"
    /// cannot disagree about what is pending. Null when there is nothing pending, or when the selected tab
    /// has no connection to name as the target.
    /// </summary>
    public WriteConfirmation? PendingEditsConfirmation(ResultSetViewModel rs)
    {
        if (Selected is not { } tab || _ctx.EffectiveConnection(tab) is not { } connection) return null;
        var statements = PendingWriteStatements(rs);
        return statements.Count == 0 ? null : WriteConfirmation.ForEdits(connection, statements);
    }

    private static IReadOnlyList<WriteStatement> WriteStatements(
        ProviderTraits traits, IReadOnlyList<ResultEditModel.PendingChange> changes)
        => changes
            .Select(c => new WriteStatement(
                c.Kind.ToString().ToUpperInvariant(),
                ResultEditModel.InlineParameters(traits, c.Command) + ";",
                IsRisky: true))
            .ToList();

    /// <summary>
    /// The dialect of the selected tab's connection — what the editor must read the buffer with to know
    /// where a statement ends (Run's statement-at-caret, the highlight margin, folding, completion's
    /// statement scope). The sibling of <see cref="SnapshotForSelectedTab"/>, and asked per keystroke for
    /// the same reason: one editor serves every tab, so the engine changes as the selection does.
    /// <para>
    /// Never null — <see cref="ProviderTraits.For(ConnectionInfo?)"/> answers Postgres for a tab with no
    /// connection, which is what the editor did before there was a second engine.
    /// </para>
    /// </summary>
    public ISqlDialect DialectForSelectedTab()
        => ProviderTraits.For(Selected is { } tab ? _ctx.EffectiveConnection(tab) : null).Dialect;

    /// <summary>
    /// Schema for the selected tab's connection + database (drives completion); null only when it has never
    /// been read. Falls back to the snapshot cache when no session is live: completion needs the catalog, not
    /// the connection, so a disconnect / credential expiry / idle sweep must not silently switch it off.
    /// </summary>
    public ISchemaSnapshot? SnapshotForSelectedTab()
    {
        if (Selected is not { } tab || _ctx.EffectiveConnection(tab) is not { } info) return null;

        // The session key covers the database (§9.4), so this can only ever be the snapshot for *this*
        // database — which matters because it feeds editability and FK navigation, not just the popup.
        if (_ctx.Sessions.TryGet(SessionKey.For(info)) is { Snapshot: { } live }) return live;

        return _ctx.Sessions.TryGetSnapshot(info.Id, info.Database);
    }

    // History logs one entry per submitted run; a multi-statement run aggregates its sets.
    private void LogExecution(
        ConnectionInfo info, string sql, IReadOnlyList<QueryResult> results, string? transactionId)
        => _ctx.QueryLog.Append(new QueryLogEntry
        {
            ExecutedAt = DateTimeOffset.UtcNow,
            ProviderId = info.ProviderId,
            ConnectionName = info.Name,
            // Id and environment as they are right now (#113): the id survives a later rename, and the
            // environment is a fact about this execution that re-pointing the connection must not rewrite.
            ConnectionId = info.Id,
            Environment = string.IsNullOrWhiteSpace(info.Environment) ? null : info.Environment,
            Database = info.Database,
            SqlText = sql,
            Duration = results[^1].Duration,
            RowCount = results.Sum(r => r.RowCount),
            Success = results.All(r => r.Success),
            ErrorMessage = results.FirstOrDefault(r => !r.Success)?.Error?.Message,
            // Null when the statement committed itself, which is every run on an ordinary connection (#131).
            TransactionId = transactionId,
        });
}
