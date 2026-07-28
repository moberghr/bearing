using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;

namespace Squirrel.App.ViewModels;

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

    /// <summary>True when this is a single SELECT that can be paged/counted.</summary>
    public bool IsPageable { get; }

    /// <summary>The exact SELECT that produced this set (for paging/count); null unless pageable.</summary>
    public string? SourceSql { get; }

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

    // Pending change tracking, keyed by row-array reference (rows are plain object?[] with no identity).
    private readonly Dictionary<object?[], object?[]> _originals = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object?[]> _edited = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object?[]> _newRows = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object?[]> _deleted = new(ReferenceEqualityComparer.Instance);

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
        if (!_deleted.Remove(row))   // not marked → mark it (delete supersedes pending edits)
        {
            _deleted.Add(row);
            _edited.Remove(row);
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
        foreach (var row in _edited)
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
        RaisePending();
    }

    private void RaisePending()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingText));
    }
}
