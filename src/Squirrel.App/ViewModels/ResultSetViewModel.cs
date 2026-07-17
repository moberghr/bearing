using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.Core.Data;

namespace Squirrel.App.ViewModels;

/// <summary>
/// One result set from a run, with its own mutable row buffer so paging can append in place.
/// A grid result exposes <see cref="Columns"/> + <see cref="Rows"/>; non-query / error results
/// carry <see cref="Message"/> / <see cref="Error"/> instead. When <see cref="IsPageable"/> the
/// footer offers "load more" / "count", driven by <see cref="MainWindowViewModel"/> against
/// <see cref="SourceSql"/>. This is the seed of the per-result-set model Phases 2–3 build on
/// (base-table mapping, editability, PK).
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

    /// <summary>Last page came back full — more rows likely exist beyond what's loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private bool _hasMore;

    /// <summary>Total row count of the source query; null until the user asks for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    [NotifyPropertyChangedFor(nameof(CanCount))]
    private long? _totalCount;

    /// <summary>Rows loaded so far — also the offset for the next page.</summary>
    public int Loaded => Rows.Count;

    /// <summary>The [Count] button is offered only while the total is still unknown.</summary>
    public bool CanCount => IsPageable && TotalCount is null;

    public string FooterText =>
        $"Loaded {Loaded}{(HasMore ? "+" : "")}{(TotalCount is { } t ? $" of {t}" : "")} rows";

    /// <summary>Append a freshly-fetched page and update whether more remain.</summary>
    public void AppendPage(IReadOnlyList<object?[]> rows, bool hasMore)
    {
        foreach (var row in rows) Rows.Add(row);
        HasMore = hasMore;
        OnPropertyChanged(nameof(Loaded));
        OnPropertyChanged(nameof(FooterText));
    }
}
