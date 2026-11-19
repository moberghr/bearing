using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Results;
using Bearing.Core.Data;

namespace Bearing.App.ViewModels;

/// <summary>
/// One result set's client-side filter and sort (#106, design v4), and the rows they leave on screen.
/// <para>
/// <see cref="ViewRows"/> is what the grid shows; the result's own <c>Rows</c> stay every loaded row, which is
/// what pending changes, saving and exports read — a filter hides rows, it never drops them. With nothing
/// active the view mirrors the loaded rows change by change, so paging appends without a reset; with a filter
/// or sort active it is rebuilt by <see cref="RowView.Apply"/>, except that a new unsaved row goes straight to
/// the bottom, where the design pins it.
/// </para>
/// <para>
/// State is per result set and dies with it: a re-run is a new result and starts unfiltered.
/// </para>
/// </summary>
public sealed partial class ResultFilterViewModel : ObservableObject
{
    private readonly ObservableCollection<object?[]> _rows;
    private readonly Func<object?[], bool> _isNew;
    private readonly IReadOnlyList<ColumnDescriptor> _columns;
    private readonly Dictionary<int, string> _columnFilters = new();
    private int _deferDepth;
    private bool _dirty;

    public ResultFilterViewModel(
        ObservableCollection<object?[]> rows, IReadOnlyList<ColumnDescriptor> columns, Func<object?[], bool> isNew)
    {
        _rows = rows;
        _columns = columns;
        _isNew = isNew;
        ViewRows = new ViewRowCollection(rows);
        rows.CollectionChanged += OnRowsChanged;
    }

    /// <summary>The rows on screen, in view order. Bound as the grid's ItemsSource.</summary>
    public ViewRowCollection ViewRows { get; }

    /// <summary>Raised when the filter or sort changed what is shown or its order — the grid clears its
    /// selection, as the design asks, since a selection over rows that moved or vanished means nothing.</summary>
    public event Action? ViewChanged;

    [ObservableProperty] private bool _isFilterOpen;
    [ObservableProperty] private bool _isColumnFilterRowOpen;
    [ObservableProperty] private string _quickFilter = "";
    [ObservableProperty] private RowSort? _sort;

    /// <summary>The column expressions with text in them, by result column index.</summary>
    public IReadOnlyDictionary<int, string> ColumnFilters => _columnFilters;

    public string ColumnFilterText(int column) => _columnFilters.TryGetValue(column, out var t) ? t : "";

    public int ActiveColumnFilterCount => _columnFilters.Count;

    public bool HasColumnFilters => ActiveColumnFilterCount > 0;

    /// <summary>A filter of either kind is narrowing the rows.</summary>
    public bool IsFiltered => QuickFilter.Trim().Length > 0 || HasColumnFilters;

    /// <summary>Anything at all changes the view — what auto-load and the mirror path check.</summary>
    public bool IsActive => IsFiltered || Sort is not null;

    public int ShownCount => ViewRows.Count;
    public int LoadedCount => _rows.Count;

    /// <summary>The filter bar's match count while filtered: "4 / 6 loaded".</summary>
    public string MatchText => $"{ShownCount:N0} / {LoadedCount:N0} loaded";

    public string MatchTip =>
        $"Filtered in the browser, on the {LoadedCount:N0} rows already loaded — nothing was re-queried.";

    /// <summary>The footer's addition while filtered: "4 shown (filtered locally)", or null.</summary>
    public string? FooterText => IsFiltered ? $"{ShownCount:N0} shown (filtered locally)" : null;

    /// <summary>Filters are on and no loaded row passes them — the empty state.</summary>
    public bool IsEmptyResult => IsFiltered && ViewRows.All(_isNew);

    public bool IsSortable(int column) => column >= 0 && column < _columns.Count && RowView.IsSortable(_columns[column]);

    /// <summary>The sort indicator was clicked on this column: ascending → descending → off.</summary>
    public void CycleSort(int column)
    {
        if (!IsSortable(column)) return;
        Sort = RowView.Next(Sort, column);
    }

    /// <summary>The header menu's Sort ascending / Sort descending: this column, this way.</summary>
    public void SortBy(int column, SortDirection direction)
    {
        if (IsSortable(column)) Sort = new RowSort(column, direction);
    }

    /// <summary>
    /// Set by the app: how a rebuild caused by typing is put off until the typing pauses — given the delay and
    /// the work, it returns a handle that cancels it. Each filter starts from this; null rebuilds at once.
    /// </summary>
    public static Func<TimeSpan, Action, IDisposable>? DefaultDebouncer { get; set; }

    /// <summary>This filter's debouncer (<see cref="DefaultDebouncer"/> unless a test hands it its own).</summary>
    public Func<TimeSpan, Action, IDisposable>? Debouncer { get; set; } = DefaultDebouncer;

    /// <summary>Loaded cells (rows × columns) above which a typed filter waits for a pause. Below it a rebuild is
    /// cheaper than a keystroke; above it, every keystroke formatting every cell is what makes typing lag.</summary>
    internal const int DebounceCells = 50_000;

    internal static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    private IDisposable? _pendingTyped;

    /// <summary>A filter's text changed: rebuild now, or once typing pauses on a large result.</summary>
    private void RefreshTyped()
    {
        _pendingTyped?.Dispose();
        _pendingTyped = null;
        if (Debouncer is not { } debounce || _rows.Count * Math.Max(1, _columns.Count) < DebounceCells)
        {
            Refresh(changedView: true);
            return;
        }
        _pendingTyped = debounce(DebounceDelay, () =>
        {
            _pendingTyped = null;
            Refresh(changedView: true);
        });
    }

    public void SetColumnFilter(int column, string? text)
    {
        var had = ColumnFilterText(column);
        var now = text?.Trim() ?? "";
        if (had == now) return;
        if (now.Length == 0) _columnFilters.Remove(column); else _columnFilters[column] = now;
        OnPropertyChanged(nameof(ColumnFilters));
        OnPropertyChanged(nameof(ActiveColumnFilterCount));
        OnPropertyChanged(nameof(HasColumnFilters));
        RefreshTyped();
    }

    /// <summary>Ctrl/⌘+F or the toolbar button: open the bar, and put the caret in its input even when it was
    /// already open.</summary>
    public void Open()
    {
        IsFilterOpen = true;
        FocusRequested?.Invoke();
    }

    /// <summary>The bar's input should take the keyboard (raised by <see cref="Open"/>).</summary>
    public event Action? FocusRequested;

    /// <summary>
    /// Esc in the quick-filter input: clears its text first; with no text and no column filters, closes the bar.
    /// The bar can't close over an active column filter — "active state is never hidden" — only ✕ does that.
    /// Returns whether Esc did anything.
    /// </summary>
    public bool Escape()
    {
        if (QuickFilter.Length > 0) { QuickFilter = ""; return true; }
        if (HasColumnFilters) return false;
        if (!IsFilterOpen) return false;
        IsFilterOpen = false;
        IsColumnFilterRowOpen = false;
        return true;
    }

    /// <summary>✕: clear every filter and close the bar. The sort is left alone — it has its own control.</summary>
    public void ClearAndClose()
    {
        var hadFilters = IsFiltered;
        _columnFilters.Clear();
        _clearing = true;
        try { QuickFilter = ""; } finally { _clearing = false; }
        OnPropertyChanged(nameof(ColumnFilters));
        OnPropertyChanged(nameof(ActiveColumnFilterCount));
        OnPropertyChanged(nameof(HasColumnFilters));
        IsColumnFilterRowOpen = false;
        IsFilterOpen = false;
        if (hadFilters) Refresh(changedView: true);
    }

    private bool _clearing;

    partial void OnQuickFilterChanged(string value)
    {
        if (!_clearing) RefreshTyped();
    }

    partial void OnSortChanged(RowSort? value) => Refresh(changedView: true);

    /// <summary>Batch many row changes into one rebuild — a page of rows arrives one add at a time.</summary>
    public IDisposable DeferRefresh()
    {
        _deferDepth++;
        return new Deferral(this);
    }

    private sealed class Deferral(ResultFilterViewModel owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--owner._deferDepth == 0 && owner._dirty) owner.Refresh(changedView: false);
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsActive)
        {
            ViewRows.Mirror(e, _rows);
            RaiseCounts();
            return;
        }
        switch (e.Action)
        {
            // A new unsaved row is pinned at the bottom whatever the sort, so it needn't rebuild anything —
            // and must not: the grid is about to focus it.
            case NotifyCollectionChangedAction.Add when e.NewItems!.Cast<object?[]>().All(_isNew):
                foreach (object?[] row in e.NewItems!) ViewRows.Add(row);
                RaiseCounts();
                return;
            case NotifyCollectionChangedAction.Remove:
                foreach (object?[] row in e.OldItems!) ViewRows.Remove(row);
                RaiseCounts();
                return;
            // A saved row's array swapped for the committed one: same place on screen.
            case NotifyCollectionChangedAction.Replace:
                for (var i = 0; i < e.OldItems!.Count; i++)
                {
                    var at = ViewRows.IndexOf((object?[])e.OldItems[i]!);
                    if (at >= 0) ViewRows[at] = (object?[])e.NewItems![i]!;
                }
                RaiseCounts();
                return;
        }
        Refresh(changedView: false);
    }

    /// <summary>Rebuild the view from the loaded rows.</summary>
    /// <param name="changedView">The filter or sort itself changed (so selection is cleared), rather than the
    /// rows underneath them.</param>
    public void Refresh(bool changedView)
    {
        if (_deferDepth > 0) { _dirty = true; return; }
        _dirty = false;
        // This rebuild reads the current text, so a typed rebuild still waiting would only repeat it.
        _pendingTyped?.Dispose();
        _pendingTyped = null;
        if (IsActive) ViewRows.ResetTo(RowView.Apply(_rows, _isNew, QuickFilter, _columnFilters, Sort));
        else if (!ViewRows.SequenceEqual(_rows)) ViewRows.ResetTo(_rows);
        RaiseCounts();
        if (changedView) ViewChanged?.Invoke();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ShownCount));
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(MatchText));
        OnPropertyChanged(nameof(MatchTip));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsEmptyResult));
    }
}

/// <summary>
/// The view's row list: an <see cref="ObservableCollection{T}"/> that can also be replaced wholesale with a
/// single Reset, so a filter keystroke over thousands of rows is one grid refresh rather than one per row.
/// </summary>
public sealed class ViewRowCollection : ObservableCollection<object?[]>
{
    public ViewRowCollection(IEnumerable<object?[]> rows) : base(rows) { }

    public void ResetTo(IEnumerable<object?[]> rows)
    {
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Apply a change made to the loaded rows, unfiltered: same positions, same items.</summary>
    internal void Mirror(NotifyCollectionChangedEventArgs e, IEnumerable<object?[]> source)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                var at = e.NewStartingIndex < 0 ? Count : e.NewStartingIndex;
                foreach (object?[] row in e.NewItems!) Insert(at++, row);
                break;
            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < e.OldItems!.Count; i++) RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace:
                for (var i = 0; i < e.NewItems!.Count; i++) this[e.NewStartingIndex + i] = (object?[])e.NewItems[i]!;
                break;
            case NotifyCollectionChangedAction.Move:
                Move(e.OldStartingIndex, e.NewStartingIndex);
                break;
            default:
                ResetTo(source);
                break;
        }
    }
}
