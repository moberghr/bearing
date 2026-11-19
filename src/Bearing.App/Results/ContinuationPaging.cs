using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Sessions;

namespace Bearing.App.Results;

/// <summary>
/// Load more and fetch all for a result the engine continues by token (<see cref="QueryResult.Continuation"/>)
/// rather than by <c>LIMIT</c>/<c>OFFSET</c> — DynamoDB's <c>NextToken</c>.
/// <para>
/// Every trip after the first page sends the token and nothing else, through
/// <see cref="IQueryExecutor.ExecuteContinuationAsync"/> only: the statement is not re-run, and rows already on
/// screen are never read again. Whether more rows exist is the token's answer, never the row count's — a page
/// cut short by the engine's request budget may hold fewer rows than a page and still have more behind it.
/// </para>
/// <para>
/// Lifted out of <see cref="ExecutionViewModel"/> (§9.1), which keeps one delegating branch per entry point.
/// The pieces that must stay single — the lease on the live session, the executor chooser, the per-tab run
/// lifecycle and where a terminal message goes — are the view model's own, handed in rather than copied.
/// </para>
/// </summary>
internal sealed class ContinuationPaging
{
    /// <summary>The view model's per-tab single-flight run: busy flag, cancellation, and the
    /// "<c>{cancelled}</c>" / "<c>{failed}: {message}</c>" status for a cancel or a throw.</summary>
    internal delegate Task RunExclusive(
        EditorTabViewModel tab, Func<CancellationToken, Task> body, string cancelled, string failed);

    private readonly WorkspaceContext _ctx;
    private readonly Func<SessionLease?> _resolveLease;
    private readonly Func<EditorTabViewModel, ConnectionSession, IQueryExecutor> _executorFor;
    private readonly RunExclusive _runExclusive;
    private readonly Action<EditorTabViewModel, string> _runStatus;
    private readonly Action<EditorTabViewModel, string> _runFinished;
    private readonly Action<EditorTabViewModel, int> _reportCeiling;
    private readonly Func<ConnectionInfo, QueryError, DbErrorKind> _classify;

    /// <param name="resolveLease">A lease on the selected tab's live session, or null (status already set)
    /// when it has none — the same lease load more takes on every engine (§5.3).</param>
    /// <param name="executorFor">The one executor chooser (§1.10): the tab's held transaction, else the pool.</param>
    /// <param name="runStatus">Progress for a tab — the status bar only while that tab is on screen.</param>
    /// <param name="runFinished">A terminal message — the status bar, or a toast for a tab off screen.</param>
    /// <param name="reportCeiling">The existing fetch-all stop at the row ceiling, so both read alike.</param>
    /// <param name="classify">The engine's verdict on a failed page.</param>
    public ContinuationPaging(
        WorkspaceContext ctx,
        Func<SessionLease?> resolveLease,
        Func<EditorTabViewModel, ConnectionSession, IQueryExecutor> executorFor,
        RunExclusive runExclusive,
        Action<EditorTabViewModel, string> runStatus,
        Action<EditorTabViewModel, string> runFinished,
        Action<EditorTabViewModel, int> reportCeiling,
        Func<ConnectionInfo, QueryError, DbErrorKind> classify)
    {
        _ctx = ctx;
        _resolveLease = resolveLease;
        _executorFor = executorFor;
        _runExclusive = runExclusive;
        _runStatus = runStatus;
        _runFinished = runFinished;
        _reportCeiling = reportCeiling;
        _classify = classify;
    }

    private int PageSize => _ctx.Settings.ResultPageSize;

    /// <summary>Append the next page (infinite-scroll "load more") and replace the token with the one it
    /// carries. A page the engine's request budget cut short says so in the status line.</summary>
    public async Task LoadMoreAsync(ResultSetViewModel rs)
    {
        var tab = _ctx.SelectedTab;
        if (tab is null || tab.IsRunning || !rs.HasMore || rs.Continuation is not { } token) return;
        using var lease = _resolveLease();
        if (lease is null) return;
        var session = lease.Session;

        await _runExclusive(tab, async ct =>
        {
            var page = await _executorFor(tab, session).ExecuteContinuationAsync(token, PageSize, ct);

            // A failed page leaves the rows, the token and HasMore as they were, so the next scroll retries —
            // the same reasoning, and the same three answers, as the offset path in ExecutionViewModel.
            if (!page.Success)
            {
                ct.ThrowIfCancellationRequested();
                if (page.Error is { } stopped && _classify(session.Info, stopped) == DbErrorKind.Canceled)
                {
                    _runStatus(tab, $"Load stopped: {stopped.Message}");
                    return;
                }

                _runFinished(tab, $"Load more failed: {page.Error?.Message}");
                return;
            }

            rs.AppendContinuationPage(page.Rows, page.Continuation);
            // Silent on success like the offset path (auto-load fires on scroll), except for the one thing a
            // short page cannot say by itself: that it is short because the read was cut, not because it ended.
            if (BudgetNotice(page) is { } notice) _runStatus(tab, notice);
        }, "Load cancelled.", "Load more failed");
    }

    /// <summary>
    /// Follow the token to the end, or to the Fetch-all row limit (<c>ResultFetchAllMaxRows</c>), whichever
    /// comes first — reported as a stop, not a success, exactly as the offset fetch all reports it.
    /// </summary>
    /// <returns>True only when the read is exhausted (the token is gone) — false if it was cancelled, failed,
    /// or stopped at the ceiling, so an Export chained off it does not write half a result.</returns>
    public async Task<bool> FetchAllAsync(ResultSetViewModel rs)
    {
        var tab = _ctx.SelectedTab;
        if (tab is null || tab.IsRunning) return false;
        if (rs.Continuation is null) return !rs.HasMore;
        using var lease = _resolveLease();
        if (lease is null) return false;
        var session = lease.Session;
        var cap = Math.Max(1, _ctx.Settings.ResultFetchAllMaxRows);
        var complete = false;

        await _runExclusive(tab, async ct =>
        {
            var executor = _executorFor(tab, session);

            // No ConfigureAwait(false): each page is appended to Rows, which the grid is bound to.
            while (rs.Continuation is { } token)
            {
                // A token still in hand with no room left is the ceiling: there may be more, and saying
                // "fetched all" here would make the row count — and an export taken from it — quietly wrong.
                var room = cap - rs.Loaded;
                if (room <= 0)
                {
                    _reportCeiling(tab, cap);
                    return;
                }

                var page = await executor.ExecuteContinuationAsync(token, Math.Min(PageSize, room), ct);
                if (!page.Success)
                {
                    ct.ThrowIfCancellationRequested();
                    _runFinished(tab, $"Fetch all failed: {page.Error?.Message}");
                    return;
                }

                rs.AppendContinuationPage(page.Rows, page.Continuation);
                _runStatus(tab, $"Fetching all rows… {rs.Loaded:N0} so far (Esc to stop)");
            }

            complete = true;
            // The total is now a fact — it is what was loaded — so [Count] retires without a query.
            rs.TotalCount = rs.Loaded;
            _runFinished(tab, $"Fetched all {rs.Loaded:N0} rows.");
        }, "Fetch all cancelled.", "Fetch all failed");

        return complete;
    }

    /// <summary>The engine's own sentence for a page its request budget cut short — carried in
    /// <see cref="QueryResult.Message"/> on a result that can still continue — with this host's way to read on
    /// (<see cref="LoadMoreContinues"/>): the engine's sentence names no button, since the <c>bearing</c> command
    /// has none. Null for any other page.</summary>
    internal static string? BudgetNotice(QueryResult page)
        => page is { Success: true, Continuation: not null, Message: { Length: > 0 } message }
            ? $"{message.TrimEnd()} {LoadMoreContinues}"
            : null;

    /// <summary>What the App appends to a budget notice: how to read on from where the page stopped.</summary>
    internal const string LoadMoreContinues = "Load more continues from where it stopped.";
}
