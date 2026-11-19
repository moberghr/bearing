using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Bearing.App.Results;

/// <summary>
/// A result set's rows, as the grid binds them — an <see cref="ObservableCollection{T}"/> that can also take a
/// batch of rows with <b>one</b> notification.
/// <para>
/// Why it exists: Avalonia's <c>DataGridCollectionView</c> answers each single-row <c>Add</c> with an
/// <c>IndexOf</c> over every row it already holds (and a multi-item <c>Add</c> is the same loop), so appending
/// <i>n</i> rows one by one costs O(n²). A streamed batch or a Fetch all of 150,000 rows pinned the UI thread for
/// minutes (measured, §5.8). A <c>Reset</c> makes the view rebuild its list once — O(n) per batch.
/// </para>
/// </summary>
public sealed class RowCollection : ObservableCollection<object?[]>
{
    public RowCollection() { }

    public RowCollection(IEnumerable<object?[]> rows) : base(rows) { }

    /// <summary>Append <paramref name="rows"/> in order, raising a single <c>Reset</c>.</summary>
    public void AppendRange(IReadOnlyList<object?[]> rows)
    {
        if (rows.Count == 0) return;
        CheckReentrancy();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
