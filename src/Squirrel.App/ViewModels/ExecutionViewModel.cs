using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.App.Connections;
using Squirrel.App.Results;
using Squirrel.App.Services;
using Squirrel.App.Workspace;
using Squirrel.Core.Data;
using Squirrel.Core.Logging;
using Squirrel.Core.Schema;
using Squirrel.Sql;

namespace Squirrel.App.ViewModels;

/// <summary>One generated write statement, tagged INSERT/UPDATE/DELETE for the floating script panel.</summary>
public sealed record PendingStatement(string Kind, string Sql);

/// <summary>
/// The execution concern: running the selected tab's SQL, paging, count, foreign-key navigation, and the
/// inline-edit save/discard/preview. Owns the in-flight cancellation and the busy flag. Extracted from the
/// shell (docs/mvvm-refactor-plan.md phase 3); coordinates through <see cref="WorkspaceContext"/> and reads
/// the selected tab from it (moved into the context in phase 4). Pure DML/result shaping stays in Results/
/// (ResultEditModel, ResultSetBuilder).
/// </summary>
public sealed partial class ExecutionViewModel : ObservableObject
{
    /// <summary>Default page size: first page and each "load more" fetch this many rows.</summary>
    public const int PageSize = 100;

    private readonly WorkspaceContext _ctx;
    private readonly IDialogService? _dialogs;
    private CancellationTokenSource? _executionCts;

    public ExecutionViewModel(WorkspaceContext ctx, IDialogService? dialogs)
    {
        _ctx = ctx;
        _dialogs = dialogs;
    }

    private EditorTabViewModel? Selected => _ctx.SelectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    private bool _isBusy;

    /// <summary>The Run button doubles as Cancel while a query is in flight.</summary>
    public string RunButtonText => IsBusy ? "Cancel (Esc)" : "Run (Ctrl+Enter)";

    /// <summary>Execute SQL for the selected tab against that tab's connection; record it in the log.</summary>
    public async Task ExecuteAsync(string sql)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(sql)) return;
        var tab = Selected;
        if (tab is null) { _ctx.SetStatus("No editor."); return; }
        if (tab.ConnectionId is null) { _ctx.SetStatus("This tab has no connection — pick one."); return; }
        var info = _ctx.EffectiveConnection(tab);
        if (info is null) { _ctx.SetStatus("Connection no longer exists."); return; }

        // Production write-guard: confirm before writing data / altering schema on a guarded connection.
        if (info.RequireWriteConfirmation && _dialogs is { } dialogs)
        {
            var risky = WriteGuard.FindRiskyStatements(sql);
            if (risky.Count > 0 && !await dialogs.ConfirmWriteAsync(info, risky))
            {
                _ctx.SetStatus("Cancelled — write not confirmed.");
                return;
            }
        }

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        var wall = Stopwatch.StartNew();
        try
        {
            // Acquire a lease so an idle sweep / evict / database switch can't dispose the pool from
            // under this query while it runs (the lease is held for the whole read).
            SessionLease lease;
            try { lease = await _ctx.Sessions.AcquireAsync(info, ct); }
            catch (ConnectionFailedException ex) { _ctx.IsConnected = false; _ctx.SetStatus(ex.Message); return; }

            using (lease)
            {
                var session = lease.Session;
                _ctx.IsConnected = true;
                _ = _ctx.Sessions.EnsureSchemaAsync(session, CancellationToken.None); // warm completion, don't block Run

                _ctx.SetStatus("Running…");
                // Push a server-side LIMIT for a single read-only SELECT so a remote server produces only
                // ~one page instead of streaming the whole result. Fetch one extra row (PageSize+1) so the
                // executor's Truncated flag still signals "more rows exist". Writes / multi-statement /
                // already-limited queries return null here and run unbounded (capped client-side). The set
                // still pages/counts against the original sql, so SourceSql is unchanged.
                var fetchSql = FirstPageLimiter.TryAppendLimit(sql, PageSize + 1) ?? sql;
                var results = await session.Executor.ExecuteAsync(fetchSql, new QueryOptions { MaxRows = PageSize }, ct);
                wall.Stop();
                tab.SetFreshResults(ResultSetBuilder.BuildResultSets(results, sql, session.Snapshot));
                LogExecution(info, sql, results);
                var summary = ResultSetBuilder.DescribeResults(results, wall.Elapsed);
                // On success, lead with the connection so the status bar reads e.g. "pagila (local) · 88 ms".
                _ctx.SetStatus(results.Any(r => !r.Success) ? summary : $"{info.Name} · {summary}");
            }
        }
        catch (OperationCanceledException) { _ctx.SetStatus("Query cancelled."); }
        catch (Exception ex) { _ctx.SetStatus($"Execution error: {ex.Message}"); }
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
        catch (OperationCanceledException) { _ctx.SetStatus("Load cancelled."); }
        catch (Exception ex) { _ctx.SetStatus($"Load more failed: {ex.Message}"); }
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
            _ctx.SetStatus(rs.TotalCount is not null ? "Counted total." : "Count unavailable for this query.");
        }
        catch (OperationCanceledException) { _ctx.SetStatus("Count cancelled."); }
        catch (Exception ex) { _ctx.SetStatus($"Count failed: {ex.Message}"); }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    /// <summary>A lease on the already-connected session for the selected tab (paging/count/nav/save run
    /// post-execute, so the connection is live). Keeps the session from being disposed by an idle sweep /
    /// evict while the follow-up runs — dispose it when done. Null (with a status set) if the tab lost its
    /// connection.</summary>
    private SessionLease? ResolveLiveLease()
    {
        if (Selected?.ConnectionId is { } id && _ctx.Sessions.TryGet(id) is { } session)
            return _ctx.Sessions.Lease(session);
        _ctx.SetStatus("Not connected.");
        return null;
    }

    /// <summary>Navigate a foreign-key cell in place: run the lookup on the current tab's connection and
    /// swap the displayed result for the referenced row, stacking the previous result so Back can return.
    /// The query is never surfaced in the editor.</summary>
    public async Task NavigateForeignKeyAsync(ResultSetViewModel rs, int columnIndex, object?[] row)
    {
        if (IsBusy) return;
        if (columnIndex < 0 || columnIndex >= row.Length) return;
        if (row[columnIndex] is null) { _ctx.SetStatus("Empty key — nothing to navigate to."); return; }
        if (Selected is not { } tab) return;
        if (SnapshotForSelectedTab() is not { } snapshot) { _ctx.SetStatus("Schema not loaded yet."); return; }
        if (ForeignKeyResolver.Resolve(snapshot, rs.Columns, columnIndex) is not { } target)
        { _ctx.SetStatus("Not a foreign key."); return; }
        using var lease = ResolveLiveLease();
        if (lease is null) return;
        var session = lease.Session;

        var sql = ResultEditModel.BuildForeignKeySelect(target, row);
        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            _ctx.SetStatus("Opening referenced row…");
            var results = await session.Executor.ExecuteAsync(sql, new QueryOptions { MaxRows = PageSize }, ct);
            tab.PushResults(ResultSetBuilder.BuildResultSets(results, sql, session.Snapshot));
            _ctx.SetStatus(ResultSetBuilder.DescribeResults(results));
        }
        catch (OperationCanceledException) { _ctx.SetStatus("Navigation cancelled."); }
        catch (Exception ex) { _ctx.SetStatus($"Navigation failed: {ex.Message}"); }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    // ---- Inline editing — connection/transaction/status concerns; pure DML lives in Results/ResultEditModel.

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

        // Production write-guard: inline grid saves are always data-modifying (INSERT/UPDATE/DELETE),
        // so confirm before committing them on a guarded connection — mirrors the ExecuteAsync gate.
        if (Selected is { } sel && _ctx.EffectiveConnection(sel) is { RequireWriteConfirmation: true } guarded
            && _dialogs is { } dialogs)
        {
            var verbs = changes.Select(c => c.Kind.ToString().ToUpperInvariant()).Distinct().ToList();
            if (!await dialogs.ConfirmWriteAsync(guarded, verbs))
            {
                _ctx.SetStatus("Cancelled — write not confirmed.");
                return;
            }
        }

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            _ctx.SetStatus($"Saving {changes.Count} change(s)…");
            var results = await session.Executor.ExecuteWriteAsync(changes.Select(c => c.Command).ToList(), ct);
            if (results.FirstOrDefault(r => !r.Success) is { } failed)
            { _ctx.SetStatus($"Save failed: {failed.Error?.Message}"); return; } // rows/pending untouched

            ResultEditModel.ApplySavedChanges(rs, target, changes, results);
            _ctx.SetStatus($"Saved {changes.Count} change(s).");
        }
        catch (OperationCanceledException) { _ctx.SetStatus("Save cancelled."); }
        catch (Exception ex) { _ctx.SetStatus($"Save failed: {ex.Message}"); }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    /// <summary>Discard all pending changes in place (restore edited cells, drop new rows, un-mark deletes).</summary>
    public Task DiscardChangesAsync(ResultSetViewModel rs)
    {
        if (rs.HasPendingChanges) { rs.RevertPending(); _ctx.SetStatus("Changes discarded."); }
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
        => Selected?.ConnectionId is { } id ? _ctx.Sessions.TryGet(id)?.Snapshot : null;

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
