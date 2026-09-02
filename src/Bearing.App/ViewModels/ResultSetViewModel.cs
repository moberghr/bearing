using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.App.ViewModels;

/// <summary>
/// One result set from a run, with its own mutable row buffer so paging can append in place.
/// A grid result exposes <see cref="Columns"/> + <see cref="Rows"/>; non-query / error results
/// carry <see cref="Message"/> / <see cref="Error"/> instead. When <see cref="IsPageable"/> the
/// footer offers "load more" / "count", driven by <see cref="ExecutionViewModel"/> against
/// <see cref="SourceSql"/>. Also carries the base-table mapping, editability, and PK/FK columns.
/// </summary>
public sealed partial class ResultSetViewModel : ObservableObject
{
    /// <param name="pageable">True only for a single-statement, row-returning result — then
    /// <paramref name="sourceSql"/> is the exact SELECT to page/count against.</param>
    public ResultSetViewModel(QueryResult result, string? sourceSql, bool pageable)
    {
        Columns = result.Columns;
        Rows = new ObservableCollection<object?[]>(result.Rows);
        RowCount = result.RowCount;
        Duration = result.Duration;
        Message = result.Message;
        Error = result.Error;
        Success = result.Success;
        IsPageable = pageable;
        SourceSql = pageable ? sourceSql : null;
        _hasMore = pageable && result.Truncated;
    }

    public IReadOnlyList<ColumnDescriptor> Columns { get; }

    /// <summary>Rows shown in the grid; grows as pages are appended (bound as the grid ItemsSource).</summary>
    public ObservableCollection<object?[]> Rows { get; }

    public long RowCount { get; }
    public TimeSpan Duration { get; }
    public string? Message { get; }
    public QueryError? Error { get; }
    public bool Success { get; }

    /// <summary>A row-returning success renders a grid; everything else renders as text.</summary>
    public bool HasGrid => Success && Columns.Count > 0;

    /// <summary>True when this is a single SELECT that can be paged/counted. Settable because the answer
    /// can turn out to be no <em>after</em> the first page: an engine may refuse to wrap a shape it cannot
    /// put in a derived table (a CTE on SQL Server), and <see cref="RetirePaging"/> is how that is admitted
    /// rather than retried forever.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCount))]
    private bool _isPageable;

    /// <summary>The exact SELECT that produced this set (for paging/count); null unless pageable.</summary>
    public string? SourceSql { get; }

    /// <summary>
    /// The engine this set came from — its dialect and literal style. Carried on the result rather than
    /// looked up per use because the surfaces that need it (Copy as ▸ SQL, the IN list) reach it from the
    /// grid, which knows the result and not the connection. Defaults to Postgres, which is what every
    /// caller did when there was one engine.
    /// </summary>
    public Connections.ProviderTraits Traits { get; init; } = Connections.ProviderTraits.Postgres;

    /// <summary>Column indices that are foreign keys (rendered clickable → navigate to the referenced row).
    /// Computed by the shell from the schema snapshot; empty when the connection has no snapshot yet.</summary>
    public IReadOnlyCollection<int> ForeignKeyColumns { get; init; } = System.Array.Empty<int>();

    /// <summary>Column indices that are primary keys of their base table (for the header PK badge).
    /// Independent of editability, so a locked/join result can still flag its key columns.</summary>
    public IReadOnlyCollection<int> PrimaryKeyColumns { get; init; } = System.Array.Empty<int>();

    /// <summary>When the grid is read-only, a short reason for the lock affordance; null when editable
    /// (or when editability couldn't be determined, e.g. no schema loaded).</summary>
    public string? LockReason { get; init; }

    /// <summary>Last page came back full — more rows likely exist beyond what's loaded.</summary>
    [ObservableProperty]
    private bool _hasMore;

    /// <summary>Total row count of the source query; null until the user asks for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowCountText))]
    [NotifyPropertyChangedFor(nameof(MetaDetail))]
    [NotifyPropertyChangedFor(nameof(CanCount))]
    private long? _totalCount;

    /// <summary>Rows loaded so far — also the offset for the next page.</summary>
    public int Loaded => Rows.Count;

    /// <summary>The [Count] button is offered only while the total is still unknown.</summary>
    public bool CanCount => IsPageable && TotalCount is null;

    /// <summary>Canonical live row-count phrase, e.g. "100 rows" or "200 of 1,000 rows". Every surface
    /// (meta row, footer, status bar) derives from this so the counts never drift out of sync.</summary>
    public string RowCountText =>
        $"{Loaded:N0}{(TotalCount is { } t ? $" of {t:N0}" : "")} rows";

    /// <summary>Meta-row detail: live row count + the query time ("200 of 1,000 rows · 88 ms").</summary>
    public string MetaDetail => $"{RowCountText} · {(long)System.Math.Round(Duration.TotalMilliseconds)} ms";

    /// <summary>
    /// Give up paging this result: the rows already on screen stay, and load-more, fetch-all and [Count]
    /// retire. For a query the engine cannot page at all — not for a page that merely failed, which is left
    /// retryable on the next scroll.
    /// </summary>
    public void RetirePaging()
    {
        HasMore = false;
        IsPageable = false;
    }

    /// <summary>Append a freshly-fetched page and update whether more remain.</summary>
    public void AppendPage(IReadOnlyList<object?[]> rows, bool hasMore)
    {
        foreach (var row in rows)
        {
            Rows.Add(row);
            if (IsEditable) _originals[row] = (object?[])row.Clone();
        }
        HasMore = hasMore;
        RaiseRowCount();
    }

    /// <summary>Notify the live row-count surfaces (meta row count + count-on-demand) after a change.</summary>
    private void RaiseRowCount()
    {
        OnPropertyChanged(nameof(Loaded));
        OnPropertyChanged(nameof(RowCountText));
        OnPropertyChanged(nameof(MetaDetail));
    }

    // ---- Inline editing (Phase 3) ------------------------------------------------------------

    /// <summary>The single table this result set edits, plus per-column base name + PK flag. Null when
    /// the result isn't a single-table select over a PK'd table (then the grid stays read-only).</summary>
    public EditTarget? EditTarget { get; init; }

    /// <summary>True when rows can be edited/added/deleted (a detectable single table + PK).</summary>
    public bool IsEditable => EditTarget is not null;

    /// <summary>Whether a column may hold NULL, per the catalog. True when there is no edit target to ask —
    /// a read-only result still has to *display* whatever NULLs it read.</summary>
    public bool AllowsNull(int column) => EditTarget?.AllowsNull(column) ?? true;

    // Pending change tracking, keyed by row-array reference (rows are plain object?[] with no identity).
    private readonly Dictionary<object?[], object?[]> _originals = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object?[]> _edited = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object?[]> _newRows = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object?[]> _deleted = new(ReferenceEqualityComparer.Instance);

    /// <summary>Rows whose pending edit was set aside when they were marked for deletion (delete supersedes
    /// an edit at save time). Kept so un-marking restores the edit instead of losing it — the grid still shows
    /// the edited values, so silently dropping them meant displaying changes that would never be saved.</summary>
    private readonly HashSet<object?[]> _editedUnderDelete = new(ReferenceEqualityComparer.Instance);

    public bool HasPendingChanges => _edited.Count + _newRows.Count + _deleted.Count > 0;
    public int PendingCount => _edited.Count + _newRows.Count + _deleted.Count;
    public string PendingText => PendingCount == 1 ? "1 pending change" : $"{PendingCount} pending changes";

    /// <summary>Snapshot the loaded rows' original values (call once after EditTarget is assigned).</summary>
    public void CaptureOriginals()
    {
        foreach (var r in Rows) _originals[r] = (object?[])r.Clone();
    }

    /// <summary>The stored original values for a row (pre-edit), or null for a new/untracked row.</summary>
    public object?[]? OriginalOf(object?[] row) => _originals.TryGetValue(row, out var o) ? o : null;

    public bool IsNewRow(object?[] row) => _newRows.Contains(row);
    public bool IsRowDeleted(object?[] row) => _deleted.Contains(row);
    public bool IsRowEdited(object?[] row) => _edited.Contains(row);

    public IReadOnlyCollection<object?[]> EditedRows => _edited;
    public IReadOnlyCollection<object?[]> NewRows => _newRows;
    public IReadOnlyCollection<object?[]> DeletedRows => _deleted;

    /// <summary>Commit a cell edit from the grid: write the raw value into the row buffer and mark the row
    /// edited. No-op (returns false) when the value is unchanged or the column is out of range. Coercion of
    /// the raw value to the column's CLR type happens at save time (see <c>ResultEditModel</c>), so the grid
    /// never coerces — it just hands the raw checkbox/text value here.</summary>
    public bool SetCell(object?[] row, int column, object? value)
    {
        if (column < 0 || column >= row.Length) return false;
        if (Equals(row[column], value)) return false;
        row[column] = value;
        MarkEdited(row);
        return true;
    }

    /// <summary>Record that a cell in <paramref name="row"/> changed (new rows fold into their INSERT).</summary>
    public void MarkEdited(object?[] row)
    {
        if (!IsEditable) return;
        if (!_newRows.Contains(row) && !_deleted.Contains(row)) _edited.Add(row);
        RaisePending();
    }

    /// <summary>Append a blank row to be INSERTed on save; returns it so the grid can focus it.</summary>
    public object?[] AddRow()
    {
        var row = new object?[Columns.Count];
        Rows.Add(row);
        _newRows.Add(row);
        RaiseRowCount();
        RaisePending();
        return row;
    }

    /// <summary>Toggle a row's pending-delete mark. The row stays visible (styled) and is only removed
    /// from the DB on save; a second toggle un-marks it. A not-yet-saved new row is dropped outright.</summary>
    public void ToggleDelete(object?[] row)
    {
        if (_newRows.Contains(row))
        {
            _newRows.Remove(row);
            Rows.Remove(row);
            RaiseRowCount();
            RaisePending();
            return;
        }
        if (_deleted.Remove(row))
        {
            // Un-marked: give the row its pending edit back, if the mark had taken one away.
            if (_editedUnderDelete.Remove(row)) _edited.Add(row);
        }
        else
        {
            // Marked: delete supersedes a pending edit at save time, but remember the edit so a second
            // toggle can restore it (and so RevertPending still knows to roll those cells back).
            _deleted.Add(row);
            if (_edited.Remove(row)) _editedUnderDelete.Add(row);
        }
        RaisePending();
    }

    /// <summary>Replace a saved row's array in place (committed values / RETURNING result) and reset its
    /// baseline. The ObservableCollection swap makes the grid re-render just that row.</summary>
    public void ReplaceRow(object?[] oldRow, object?[] newRow)
    {
        var i = Rows.IndexOf(oldRow);
        if (i >= 0) Rows[i] = newRow; else Rows.Add(newRow);
        _originals.Remove(oldRow);
        _originals[newRow] = (object?[])newRow.Clone();
    }

    /// <summary>Remove a saved-as-deleted row from the grid.</summary>
    public void RemoveRow(object?[] row)
    {
        Rows.Remove(row);
        _originals.Remove(row);
        RaiseRowCount();
    }

    /// <summary>Revert all pending changes in place: restore edited cells, drop new rows, un-mark deletes.</summary>
    public void RevertPending()
    {
        // Both sets hold rows whose cells were changed in the buffer — including edits currently parked by a
        // delete mark, which a revert must roll back too (they're on screen).
        foreach (var row in _edited.Concat(_editedUnderDelete))
            if (_originals.TryGetValue(row, out var original))
                Array.Copy(original, row, Math.Min(original.Length, row.Length));
        foreach (var row in _newRows)
        {
            Rows.Remove(row);
            _originals.Remove(row);
        }
        ClearPending();
        RaiseRowCount();
    }

    /// <summary>Clear all pending marks after a save (rows were already updated in place).</summary>
    public void ClearPending()
    {
        _edited.Clear();
        _newRows.Clear();
        _deleted.Clear();
        _editedUnderDelete.Clear();
        RaisePending();
    }

    private void RaisePending()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingText));
    }
}
