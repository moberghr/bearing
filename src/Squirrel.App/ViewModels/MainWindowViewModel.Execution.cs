using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.App.Connections;
using Squirrel.App.Formatting;
using Squirrel.App.Results;
using Squirrel.Core.Data;
using Squirrel.Core.Logging;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;
using Squirrel.Sql;

namespace Squirrel.App.ViewModels;
public sealed partial class MainWindowViewModel
{
    // ---- Execution ---------------------------------------------------------------------------

    /// <summary>Default page size: first page and each "load more" fetch this many rows.</summary>
    public const int PageSize = 100;

    /// <summary>
    /// Confirm a write/destructive statement before it runs against a guarded connection. Set by the
    /// view to show a dialog; args are the target connection and the risky verbs found. Returns true to
    /// proceed. When unset, guarded writes proceed (headless/tests) — the guard is a UI affordance.
    /// </summary>
    public Func<ConnectionInfo, IReadOnlyList<string>, Task<bool>>? ConfirmDangerousWrite { get; set; }

    /// <summary>Execute SQL for the selected tab against that tab's connection; record it in the log.</summary>
    public async Task ExecuteAsync(string sql)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(sql)) return;
        var tab = SelectedTab;
        if (tab is null) { StatusText = "No editor."; return; }
        if (tab.ConnectionId is null) { StatusText = "This tab has no connection — pick one."; return; }
        var info = EffectiveConnection(tab);
        if (info is null) { StatusText = "Connection no longer exists."; return; }

        // Production write-guard: confirm before writing data / altering schema on a guarded connection.
        if (info.RequireWriteConfirmation && ConfirmDangerousWrite is { } confirm)
        {
            var risky = Squirrel.Sql.WriteGuard.FindRiskyStatements(sql);
            if (risky.Count > 0 && !await confirm(info, risky))
            {
                StatusText = "Cancelled — write not confirmed.";
                return;
            }
        }

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Acquire a lease so an idle sweep / evict / database switch can't dispose the pool from
            // under this query while it runs (the lease is held for the whole read).
            SessionLease lease;
            try { lease = await _sessions.AcquireAsync(info, ct); }
            catch (ConnectionFailedException ex) { IsConnected = false; StatusText = ex.Message; return; }

            using (lease)
            {
                var session = lease.Session;
                IsConnected = true;
                _ = _sessions.EnsureSchemaAsync(session, CancellationToken.None); // warm completion, don't block Run

                StatusText = "Running…";
                // Fetch only the first page; a single row-returning statement is then pageable and
                // "load more"/"count" run against the original sql. Multi-statement runs are capped
                // per set at PageSize and shown truncated (no paging — see the pageable gate below).
                //
                // Push a server-side LIMIT for a single read-only SELECT so a remote server produces only
                // ~one page instead of computing/streaming the whole result for us to read 100 rows of and
                // discard. Fetch one extra row (PageSize+1) so the executor's Truncated flag still signals
                // "more rows exist" for load-more. Writes / multi-statement / already-limited queries return
                // null here and run unbounded (capped client-side), exactly as before. The result set still
                // pages/counts against the original sql, so SourceSql is unchanged.
                var fetchSql = Squirrel.Sql.FirstPageLimiter.TryAppendLimit(sql, PageSize + 1) ?? sql;
                var results = await session.Executor.ExecuteAsync(fetchSql, new QueryOptions { MaxRows = PageSize }, ct);
                wall.Stop();
                tab.SetFreshResults(ResultSetBuilder.BuildResultSets(results, sql, session.Snapshot));
                LogExecution(info, sql, results);
                var summary = ResultSetBuilder.DescribeResults(results, wall.Elapsed);
                // On success, lead with the connection so the status bar reads e.g. "pagila (local) · 88 ms".
                StatusText = results.Any(r => !r.Success) ? summary : $"{info.Name} · {summary}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Query cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Execution error: {ex.Message}";
        }
        finally
        {
            _executionCts.Dispose();
            _executionCts = null;
            IsBusy = false;
        }
    }

    /// <summary>Cancel the in-flight query, if any (Esc / the Run button while busy).</summary>
    public void CancelExecution()
    {
        try { _executionCts?.Cancel(); }
        catch (ObjectDisposedException) { /* completed between the null-check and Cancel */ }
    }

    /// <summary>Append the next page to a pageable result set (infinite-scroll "load more").</summary>
    public async Task LoadMoreAsync(ResultSetViewModel rs)
    {
        if (IsBusy || !rs.IsPageable || rs.SourceSql is null || !rs.HasMore) return;
        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            var page = await session.Executor.ExecutePageAsync(rs.SourceSql, rs.Loaded, PageSize, ct);
            rs.AppendPage(page.Rows, page.RowCount == PageSize);
            // No status update: auto-load fires on scroll and the count lives on the meta row.
        }
        catch (OperationCanceledException) { StatusText = "Load cancelled."; }
        catch (Exception ex) { StatusText = $"Load more failed: {ex.Message}"; }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    /// <summary>Fill in the total row count for a pageable result set (the [Count] action).</summary>
    public async Task CountTotalAsync(ResultSetViewModel rs)
    {
        if (IsBusy || !rs.IsPageable || rs.SourceSql is null) return;
        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            rs.TotalCount = await session.Executor.CountAsync(rs.SourceSql, ct);
            StatusText = rs.TotalCount is not null ? "Counted total." : "Count unavailable for this query.";
        }
        catch (OperationCanceledException) { StatusText = "Count cancelled."; }
        catch (Exception ex) { StatusText = $"Count failed: {ex.Message}"; }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    /// <summary>A lease on the already-connected session for the selected tab (paging/count/nav/save run
    /// post-execute, so the connection is live). The lease keeps the session from being disposed by an
    /// idle sweep / evict while the follow-up runs — dispose it when done. Null — with a status set —
    /// if the tab lost its connection.</summary>
    private SessionLease? ResolveLiveLease()
    {
        if (SelectedTab?.ConnectionId is { } id && _sessions.TryGet(id) is { } session)
            return _sessions.Lease(session);
        StatusText = "Not connected.";
        return null;
    }

    // ---- FK navigation -----------------------------------------------------------------------

    /// <summary>Navigate a foreign-key cell in place: run the lookup on the current tab's connection
    /// and swap the displayed result for the referenced row, stacking the previous result so Back can
    /// return to it. The query is never surfaced in the editor.</summary>
    public async Task NavigateForeignKeyAsync(ResultSetViewModel rs, int columnIndex, object?[] row)
    {
        if (IsBusy) return;
        if (columnIndex < 0 || columnIndex >= row.Length) return;
        if (row[columnIndex] is null) { StatusText = "Empty key — nothing to navigate to."; return; }
        if (SelectedTab is not { } tab) return;
        if (SnapshotForSelectedTab() is not { } snapshot) { StatusText = "Schema not loaded yet."; return; }
        if (ForeignKeyResolver.Resolve(snapshot, rs.Columns, columnIndex) is not { } target)
        { StatusText = "Not a foreign key."; return; }
        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        var sql = ResultEditModel.BuildForeignKeySelect(target, row);
        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            StatusText = "Opening referenced row…";
            var results = await session.Executor.ExecuteAsync(sql, new QueryOptions { MaxRows = PageSize }, ct);
            tab.PushResults(ResultSetBuilder.BuildResultSets(results, sql, session.Snapshot));
            StatusText = ResultSetBuilder.DescribeResults(results);
        }
        catch (OperationCanceledException) { StatusText = "Navigation cancelled."; }
        catch (Exception ex) { StatusText = $"Navigation failed: {ex.Message}"; }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    // ---- Inline editing (Phase 3) ------------------------------------------------------------
    // The pure DML/edit logic lives in Results/ResultEditModel; these methods own the connection,
    // transaction, and status-bar concerns.

    /// <summary>Apply a result set's pending edits/inserts/deletes in one transaction, then update the
    /// affected rows in place (no reload — paged-in rows and scroll are preserved).</summary>
    public async Task SaveChangesAsync(ResultSetViewModel rs)
    {
        if (IsBusy) return;
        if (rs.EditTarget is not { } target || !rs.HasPendingChanges) return;
        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        var changes = ResultEditModel.BuildPendingChanges(rs, target);
        if (changes.Count == 0) { rs.ClearPending(); return; }

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            StatusText = $"Saving {changes.Count} change(s)…";
            var results = await session.Executor.ExecuteWriteAsync(changes.Select(c => c.Command).ToList(), ct);
            if (results.FirstOrDefault(r => !r.Success) is { } failed)
            { StatusText = $"Save failed: {failed.Error?.Message}"; return; } // rows/pending untouched

            ResultEditModel.ApplySavedChanges(rs, target, changes, results);
            StatusText = $"Saved {changes.Count} change(s).";
        }
        catch (OperationCanceledException) { StatusText = "Save cancelled."; }
        catch (Exception ex) { StatusText = $"Save failed: {ex.Message}"; }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    /// <summary>Discard all pending changes in place (restore edited cells, drop new rows, un-mark deletes).</summary>
    public Task DiscardChangesAsync(ResultSetViewModel rs)
    {
        if (rs.HasPendingChanges) { rs.RevertPending(); StatusText = "Changes discarded."; }
        return Task.CompletedTask;
    }

    /// <summary>Render the write statements a save would run, values inlined, wrapped in a transaction.
    /// Null when there's nothing pending. For preview only — the real save uses parameters.</summary>
    public string? PreviewChanges(ResultSetViewModel rs)
    {
        if (rs.EditTarget is not { } target || !rs.HasPendingChanges) return null;
        var changes = ResultEditModel.BuildPendingChanges(rs, target);
        if (changes.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("begin;");
        foreach (var c in changes) sb.Append("  ").Append(ResultEditModel.InlineParameters(c.Command)).AppendLine(";");
        sb.Append("commit;");
        return sb.ToString();
    }

    /// <summary>One generated write statement, tagged INSERT/UPDATE/DELETE for the floating script panel.</summary>
    public sealed record PendingStatement(string Kind, string Sql);

    /// <summary>The pending write statements, one per dirty row, values inlined and kind-tagged (for the
    /// color-coded pending-changes script panel). Empty when there's nothing pending. Preview only.</summary>
    public IReadOnlyList<PendingStatement> PreviewChangeStatements(ResultSetViewModel rs)
    {
        if (rs.EditTarget is not { } target || !rs.HasPendingChanges) return Array.Empty<PendingStatement>();
        return ResultEditModel.BuildPendingChanges(rs, target)
            .Select(c => new PendingStatement(c.Kind.ToString().ToUpperInvariant(), ResultEditModel.InlineParameters(c.Command) + ";"))
            .ToList();
    }

    /// <summary>Schema for the selected tab's connection (drives completion); null when not yet loaded.</summary>
    public ISchemaSnapshot? SnapshotForSelectedTab()
        => SelectedTab?.ConnectionId is { } id ? _sessions.TryGet(id)?.Snapshot : null;

    public Task<IReadOnlyList<QueryLogEntry>> SearchHistoryAsync(string? text, CancellationToken ct)
        => _queryLog.SearchAsync(new QueryLogQuery { Text = text }, ct);

    // History logs one entry per submitted run; a multi-statement run aggregates its sets.
    private void LogExecution(ConnectionInfo info, string sql, IReadOnlyList<QueryResult> results) => _queryLog.Append(new QueryLogEntry
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
