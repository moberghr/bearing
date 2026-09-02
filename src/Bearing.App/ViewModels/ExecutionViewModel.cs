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
using Bearing.Core.Logging;
using Bearing.Core.Schema;
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
        OnSelectedTabChanged();
    }

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
        SyncElapsedTimer();
    }

    private void OnWatchedTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorTabViewModel.IsRunning))
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(RunButtonText));
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

        // Production write-guard: confirm before writing data / altering schema on a guarded connection.
        // Describe (not FindRiskyStatements) so the prompt can list the statements it is about to run — the
        // verbs alone can't answer "what exactly lands on prod".
        if (info.RequireWriteConfirmation && _dialogs is { } dialogs)
        {
            // The connection's dialect, not a Postgres default: the guard's verdict depends on whether it
            // can read this engine at all, and an engine it cannot read has every statement confirmed with
            // a label saying why (§1.2 — never narrower for any dialect).
            var statements = WriteGuard.Describe(traits.Dialect, sql);
            if (statements.Any(s => s.IsRisky)
                && !await dialogs.ConfirmWriteAsync(WriteConfirmation.ForBatch(info, statements)))
            {
                _ctx.SetStatus("Cancelled — write not confirmed.");
                return;
            }
        }

        // AutosaveMode.OnExecute writes the buffer at the moment it runs, so what's on disk is what was
        // executed. Before the run, not after: a query that errors or is cancelled still reflects the text
        // that produced it. No-op in the other modes.
        if (_ctx.Autosave is { } autosave) await autosave.OnExecutedAsync(tab);

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
            var outcome = await RunFetchAsync(info, tab, sql, fetchSql, final: !canRefresh, ct);
            if (outcome == RunOutcome.AuthFailed && canRefresh && !ct.IsCancellationRequested)
            {
                _ctx.Credentials.Invalidate(info.Id);
                await _ctx.Sessions.EvictAsync(SessionKey.For(info));
                RunStatus(tab, "Reauthenticating…");
                await RunFetchAsync(info, tab, sql, fetchSql, final: true, ct);
            }
        }, "Query cancelled by user.", "Execution error");
    }

    private enum RunOutcome { Success, AuthFailed, Failed }

    /// <summary>Acquire a lease, run the first-page fetch, and — unless this is a non-final attempt that hit
    /// an authentication failure — bind + log the result. Returns <see cref="RunOutcome.AuthFailed"/> without
    /// binding/logging on a non-final attempt so the caller can refresh the credential and retry once; on the
    /// final attempt any error (auth included) is surfaced normally.</summary>
    private async Task<RunOutcome> RunFetchAsync(
        ConnectionInfo info, EditorTabViewModel tab, string sql, string fetchSql, bool final, CancellationToken ct)
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

            RunStatus(tab, "Running…");
            var results = await session.Executor.ExecuteAsync(fetchSql, new QueryOptions { MaxRows = PageSize }, ct);
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
                results, sql, session.Snapshot, ProviderTraits.For(info)));
            LogExecution(info, sql, results);
            var summary = ResultSetBuilder.DescribeResults(results, wall.Elapsed);
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
            var page = await session.Executor.ExecutePageAsync(pageSql, ct);

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
            await foreach (var batch in session.Executor.StreamRowsAsync(sql, options, ct))
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
            rs.TotalCount = await session.Executor.CountAsync(countSql, ct);
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

        var suggested = ResultExport.SuggestedName(rs, tab.Header, DateTime.Now, format);
        if (await dialogs.PickExportFileAsync(suggested, format) is not { } path) return;

        // Snapshot the rows on this (UI) thread — Rows is an observable collection the grid keeps mutating —
        // then format and write off it: a 200k-row workbook is seconds of pure CPU, and doing that on the
        // dispatcher would freeze the window (the same reasoning as the data layer's ConfigureAwait pass).
        var block = TableBlock.ForResult(rs);
        var sheet = ResultExport.SheetName(rs);
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
            var results = await session.Executor.ExecuteAsync(sql, new QueryOptions { MaxRows = PageSize }, ct);
            tab.PushResults(ResultSetBuilder.BuildResultSets(
                results, sql, session.Snapshot, ProviderTraits.For(session.Info)));
            RunFinished(tab, ResultSetBuilder.DescribeResults(results));
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

        // Every inline save confirms, showing the DML it is about to commit — this is the whole preview
        // flow (there is no separate [Preview SQL] step any more), so it can't be conditional on the
        // connection's write guard. A guarded connection gets the extra warning line, not the only prompt.
        // Ahead of the lease: a modal dialog must not hold a session open while the user reads it.
        if (_ctx.EffectiveConnection(tab) is { } connection && _dialogs is { } dialogs
            && !await dialogs.ConfirmWriteAsync(WriteConfirmation.ForEdits(connection, WriteStatements(traits, changes))))
        {
            _ctx.SetStatus("Cancelled — save not confirmed.");
            return;
        }

        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        await RunExclusiveAsync(tab, async ct =>
        {
            RunStatus(tab, $"Saving {changes.Count} change(s)…");
            var results = await session.Executor.ExecuteWriteAsync(changes.Select(c => c.Command).ToList(), ct);
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

    private static IReadOnlyList<WriteStatement> WriteStatements(
        ProviderTraits traits, IReadOnlyList<ResultEditModel.PendingChange> changes)
        => changes
            .Select(c => new WriteStatement(
                c.Kind.ToString().ToUpperInvariant(),
                ResultEditModel.InlineParameters(traits, c.Command) + ";",
                IsRisky: true))
            .ToList();

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
    private void LogExecution(ConnectionInfo info, string sql, IReadOnlyList<QueryResult> results) => _ctx.QueryLog.Append(new QueryLogEntry
    {
        ExecutedAt = DateTimeOffset.UtcNow,
        ProviderId = info.ProviderId,
        ConnectionName = info.Name,
        Database = info.Database,
        SqlText = sql,
        Duration = results[^1].Duration,
        RowCount = results.Sum(r => r.RowCount),
        Success = results.All(r => r.Success),
        ErrorMessage = results.FirstOrDefault(r => !r.Success)?.Error?.Message,
    });
}
