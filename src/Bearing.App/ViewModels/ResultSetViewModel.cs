using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Sessions;

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
    /// <param name="sourceSql">The statement behind this set: the exact SELECT to page/count against when
    /// <paramref name="pageable"/>, and what <see cref="ExecutedSql"/> reports either way. For one set of a
    /// batch the caller resolves it to that set's own statement — see <c>ResultSetBuilder</c>.</param>
    /// <param name="pageable">True only for a single-statement, row-returning result — then
    /// <paramref name="sourceSql"/> is the exact SELECT to page/count against.</param>
    public ResultSetViewModel(QueryResult result, string? sourceSql, bool pageable)
    {
        Columns = result.Columns;
        Rows = new RowCollection(result.Rows);
        RowCount = result.RowCount;
        _duration = result.Duration;
        Message = result.Message;
        Error = result.Error;
        Success = result.Success;
        StatementIndex = result.StatementIndex;
        IsPageable = pageable;
        SourceSql = pageable ? sourceSql : null;
        ExecutedSql = string.IsNullOrWhiteSpace(sourceSql) ? null : sourceSql.Trim();
        Continuation = result.Continuation;
        _arrivedWithToken = result.Continuation is not null;
        // Whether or not this set can page: a set cut short must never look complete. A batch is read to the
        // Fetch all limit on its first run, so a truncated one stopped there and says so (RowCountText). A token,
        // when there is one, is the answer to "is there more" — not whether the page came back full.
        _hasMore = Continuation is not null || result.Truncated;
        ColumnLayout = new ColumnLayout(Columns.Count);
        // The marker on the meta row is derived from the layout, so it has to be told when the layout moves
        // (the layout itself is deliberately not an ObservableObject — it is pure, §2.5).
        // One handler for the result's life: the meta row's marker, and the quick filter, which searches visible
        // columns only (#106).
        Filter = new ResultFilterViewModel(Rows, Columns, IsNewRow, ColumnLayout.IsHidden);
        ColumnLayout.Changed += () =>
        {
            OnPropertyChanged(nameof(HiddenColumnsText));
            Filter.OnColumnVisibilityChanged();
        };
    }

    /// <summary>The client-side filter and sort over the loaded rows (#106), and the rows they leave on
    /// screen (<see cref="ViewRows"/>). Pending changes, saves and exports keep reading <see cref="Rows"/>.</summary>
    public ResultFilterViewModel Filter { get; }

    /// <summary>The rows on screen, in view order — the grid's ItemsSource, and the order every "next row",
    /// rectangle and paste walks. Equal to <see cref="Rows"/> while no filter or sort is active.</summary>
    public ViewRowCollection ViewRows => Filter.ViewRows;

    public IReadOnlyList<ColumnDescriptor> Columns { get; }

    /// <summary>
    /// Which columns are hidden and how many are frozen (#118). Owned per result set: it describes what is
    /// on screen, and a re-run is a different result.
    /// </summary>
    public ColumnLayout ColumnLayout { get; }

    /// <summary>The meta row's "2 columns hidden" marker, or null when nothing is hidden — so a hidden
    /// column is never a silent omission from what an export contains.</summary>
    public string? HiddenColumnsText => ColumnLayout.HiddenText;

    /// <summary>Rows shown in the grid; grows as pages are appended (bound as the grid ItemsSource).</summary>
    public RowCollection Rows { get; }

    public long RowCount { get; }

    /// <summary>The set's query time. Settable because a streamed batch builds the set as it begins and only
    /// knows the time once it is read (<see cref="EndReading"/>).</summary>
    public TimeSpan Duration
    {
        get => _duration;
        private set { _duration = value; OnPropertyChanged(nameof(MetaDetail)); }
    }
    private TimeSpan _duration;
    public string? Message { get; }
    public QueryError? Error { get; }
    public bool Success { get; }

    /// <summary>Which statement of the executed batch produced this set (0-based), as the provider proved it —
    /// <see cref="QueryResult.StatementIndex"/> verbatim. Null when the provider could not prove the mapping;
    /// never filled in by guessing.</summary>
    public int? StatementIndex { get; set; }

    /// <summary>A row-returning success renders a grid; everything else renders as text.</summary>
    public bool HasGrid => Success && Columns.Count > 0;

    /// <summary>True when this is a single SELECT that can be paged/counted. Settable because the answer
    /// can turn out to be no <em>after</em> the first page: an engine may refuse to wrap a shape it cannot
    /// put in a derived table (a CTE on SQL Server), and <see cref="RetirePaging"/> is how that is admitted
    /// rather than retried forever.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCount))]
    [NotifyPropertyChangedFor(nameof(CanFetchAll))]
    [NotifyPropertyChangedFor(nameof(IsCutShort))]
    [NotifyPropertyChangedFor(nameof(RowCountText))]
    [NotifyPropertyChangedFor(nameof(MetaDetail))]
    private bool _isPageable;

    /// <summary>The exact SELECT that produced this set (for paging/count); null unless pageable.</summary>
    public string? SourceSql { get; }

    /// <summary>The values bound to <see cref="SourceSql"/>'s placeholders (#103) — re-sent with every page,
    /// count and fetch-all, which run that same text. Empty for a query without <c>:name</c> variables.</summary>
    public IReadOnlyList<QueryParameter> SourceParameters { get; init; } = [];

    /// <summary>
    /// The engine this set came from — its dialect and literal style. Carried on the result rather than
    /// looked up per use because the surfaces that need it (Copy as ▸ SQL, the IN list) reach it from the
    /// grid, which knows the result and not the connection. Defaults to Postgres, which is what every
    /// caller did when there was one engine.
    /// </summary>
    public ProviderTraits Traits { get; init; } = ProviderTraits.Postgres;

    /// <summary>
    /// The statement text this set came from, for showing alongside the data (Copy as ▸ table with the
    /// query). Trimmed, or null when there was nothing to record. Unlike <see cref="SourceSql"/> it is kept
    /// for every result — a write, an error, one set of a batch — because "what produced this" is a question
    /// worth answering even where paging is impossible.
    /// <para>
    /// For one set out of a multi-statement run this is <b>that set's own statement</b> wherever the mapping
    /// can be proven, and the whole batch where it can't — <c>ResultSetBuilder.StatementsBehind</c> owns
    /// that decision, resolving <see cref="QueryResult.StatementIndex"/> against a lexer split of the
    /// buffer. It never reports a statement it isn't sure of.
    /// </para>
    /// <para>
    /// It is the statement the <i>user</i> ran, not quite what went over the wire: the first-page LIMIT the
    /// run injects (<c>FirstPageLimiter</c>) is our business, and pasting it into a report would misdescribe
    /// where the numbers came from.
    /// </para>
    /// </summary>
    /// <para>
    /// A run with <c>:name</c> variables reports the text the user wrote, placeholders kept, with the
    /// <c>@set</c> lines that give its values above it — the same shape history reopens it in, so what is
    /// pasted can be run again as it is (#103).
    /// </para>
    public string? ExecutedSql { get; set; }

    /// <summary>Column indices that are foreign keys (rendered clickable → navigate to the referenced row).
    /// Computed by the shell from the schema snapshot; empty when the connection has no snapshot yet.</summary>
    public IReadOnlyCollection<int> ForeignKeyColumns { get; init; } = System.Array.Empty<int>();

    /// <summary>Column indices that are primary keys of their base table (for the header PK badge).
    /// Independent of editability, so a locked/join result can still flag its key columns.</summary>
    public IReadOnlyCollection<int> PrimaryKeyColumns { get; init; } = System.Array.Empty<int>();

    /// <summary>When the grid is read-only, a short reason for the lock affordance; null when editable
    /// (or when editability couldn't be determined, e.g. no schema loaded).</summary>
    public string? LockReason { get; init; }

    /// <summary>Last page came back full — more rows likely exist beyond what's loaded. For a result the
    /// engine continues by token (<see cref="Continuation"/>), the token alone decides.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchAll))]
    [NotifyPropertyChangedFor(nameof(IsCutShort))]
    [NotifyPropertyChangedFor(nameof(RowCountText))]
    [NotifyPropertyChangedFor(nameof(MetaDetail))]
    [NotifyPropertyChangedFor(nameof(CountBadge))]
    private bool _hasMore;

    /// <summary>
    /// The engine's token for the rest of this read (<see cref="QueryResult.Continuation"/>), replaced by each
    /// page loaded after it and null once the read is exhausted. While set, load more and fetch all go through
    /// <c>ContinuationPaging</c> and send this — never the SQL again. Opaque and in memory only: never logged,
    /// never persisted.
    /// </summary>
    public string? Continuation { get; private set; }

    /// <summary>
    /// The session (connection + database) this result was read from — set by <c>ResultSetBuilder</c>. A
    /// <see cref="Continuation"/> is only ever sent back to this session: the token carries its statement, so
    /// sending it to whatever the tab is on now would run that statement there and append another server's rows
    /// to this grid. Null for a set built without one, which is then never continued (fail closed).
    /// </summary>
    public SessionKey? SourceSession { get; init; }

    /// <summary>The run that produced this set may read a whole table — it was confirmed as a possible scan, or
    /// it held an unfiltered scan whose first page ran unasked — so a fetch all or export of it asks. Set by the
    /// run after the result sets are built.</summary>
    public bool IsFullScan { get; set; }

    /// <summary>Total row count of the source query; null until the user asks for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowCountText))]
    [NotifyPropertyChangedFor(nameof(MetaDetail))]
    [NotifyPropertyChangedFor(nameof(CanCount))]
    private long? _totalCount;

    /// <summary>Rows loaded so far — also the offset for the next page.</summary>
    public int Loaded => Rows.Count;

    /// <summary>
    /// The [Count] button is offered only while the total is still unknown, and only where a count can answer:
    /// never on an engine whose reads continue by token (<see cref="ProviderTraits.ReadsContinueByToken"/> —
    /// DynamoDB has no count short of reading every match, and its executor always answers "unavailable"), nor
    /// for a result that arrived with a token, whose read the token — not an offset query — continues.
    /// </summary>
    public bool CanCount => IsPageable && TotalCount is null && !_arrivedWithToken && !Traits.ReadsContinueByToken;

    private readonly bool _arrivedWithToken;

    /// <summary>"⤓ all" / Fetch all is offered only when more rows exist <em>and</em> this set can page to them —
    /// a cut-short batch set (<see cref="IsCutShort"/>) has more rows but no way to fetch them.</summary>
    public bool CanFetchAll => IsPageable && HasMore;

    /// <summary>Canonical live row-count phrase, e.g. "100 rows" or "200 of 1,000 rows". Every surface
    /// (meta row, footer, status bar) derives from this so the counts never drift out of sync.</summary>
    public string RowCountText =>
        IsReading ? $"{Loaded:N0} rows so far…"
        : IsCutShort ? $"first {Loaded:N0} rows, more not loaded"
                   : $"{Loaded:N0}{(TotalCount is { } t ? $" of {t:N0}" : "")} rows";

    /// <summary>More rows exist and none of them can be fetched — typically a set from a batch that ran into
    /// the Fetch all limit (a batch is read to the end and never pages). Export refuses these rather than write
    /// part of the answer.</summary>
    public bool IsCutShort => HasMore && !IsPageable;

    /// <summary>Meta-row detail: live row count + the query time ("200 of 1,000 rows · 88 ms"). No time while
    /// the set is still being read — there isn't one yet.</summary>
    public string MetaDetail => IsReading
        ? RowCountText
        : $"{RowCountText} · {(long)System.Math.Round(Duration.TotalMilliseconds)} ms";

    /// <summary>
    /// True while a streamed batch is still reading this set (<see cref="BatchRunAssembler"/>): its rows are
    /// arriving, and neither "all of them" nor "cut short" is known yet. Ends with <see cref="EndReading"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowCountText))]
    [NotifyPropertyChangedFor(nameof(MetaDetail))]
    [NotifyPropertyChangedFor(nameof(CountBadge))]
    private bool _isReading;

    /// <summary>
    /// The short count a tab header carries: rows loaded, "…" while a streamed batch is still reading the set, and
    /// "+" when more exist than are loaded (another page, or a set cut short). It is the loaded count, not the
    /// set's first-read size — a streamed set begins with none, and a paged one grows.
    /// </summary>
    public string CountBadge => IsReading ? $"{Loaded:N0}…" : HasMore ? $"{Loaded:N0}+" : $"{Loaded:N0}";

    /// <summary>A streamed set has been read as far as it will be. <paramref name="more"/> is true when rows were
    /// left unread — the Fetch all ceiling, an error or Esc part-way — which a batch set can never fetch, so it
    /// then shows as cut short (<see cref="IsCutShort"/>).</summary>
    public void EndReading(bool more, TimeSpan duration)
    {
        HasMore = more;
        Duration = duration;
        IsReading = false;
    }

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
        // One rebuild for the page, not one per row, while a filter or sort is active.
        using var _ = Filter.DeferRefresh();
        // One notification for the batch, not one per row: the grid's view pays O(rows loaded) per Add
        // (RowCollection), which made a streamed batch or Fetch all quadratic. Unfiltered, the view mirrors the
        // Reset with one ResetTo of its own.
        Rows.AppendRange(rows);
        if (IsEditable)
            foreach (var row in rows) _originals[row] = (object?[])row.Clone();
        HasMore = hasMore;
        RaiseRowCount();
    }

    /// <summary>Append a page continued by token and take the token it carries in place of the one used;
    /// whether more remain follows that token (null — the read is exhausted).</summary>
    public void AppendContinuationPage(IReadOnlyList<object?[]> rows, string? continuation)
    {
        Continuation = continuation;
        AppendPage(rows, hasMore: continuation is not null);
    }

    /// <summary>Notify the live row-count surfaces (meta row count + count-on-demand) after a change.</summary>
    private void RaiseRowCount()
    {
        OnPropertyChanged(nameof(Loaded));
        OnPropertyChanged(nameof(CountBadge));
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
        // Marked new *before* it lands, so the view can see it is a pending insert and pin it at the bottom.
        _newRows.Add(row);
        Rows.Add(row);
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
            Rows.Remove(row);
            _newRows.Remove(row);
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

    /// <summary>Bumped on every change to the pending set (an edit, a new or deleted row, a revert, a save) — so
    /// whoever asked about these edits can tell later whether they changed since (the run's unsaved-edits guard).</summary>
    public int EditStamp { get; private set; }

    private void RaisePending()
    {
        EditStamp++;
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingText));
    }
}
