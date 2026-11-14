using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Bearing.App.Results;

/// <summary>
/// A result set's row buffer: an <see cref="ObservableCollection{T}"/> that can also be re-ordered in one
/// notification.
/// <para>
/// Sorting (#106) is the reason. Re-ordering through <c>Move</c> raises one change per row, and the grid
/// answers each with its own slot bookkeeping — on a fetched-all result of tens of thousands of rows that is
/// quadratic work for what is a single change of view. <see cref="ResetTo"/> swaps the contents and raises
/// one <see cref="NotifyCollectionChangedAction.Reset"/>.
/// </para>
/// </summary>
public sealed class RowCollection : ObservableCollection<object?[]>
{
    public RowCollection()
    {
    }

    public RowCollection(IEnumerable<object?[]> rows) : base(rows)
    {
    }

    /// <summary>Replace the contents with <paramref name="rows"/>, raising a single reset.</summary>
    public void ResetTo(IReadOnlyList<object?[]> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
