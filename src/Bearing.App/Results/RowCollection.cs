using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Bearing.App.Results;

/// <summary>
/// A result set's row buffer: an <see cref="ObservableCollection{T}"/> that can also be re-ordered in one
/// notification, and that remembers each row's place in the query's own order while a sort needs it (#106).
/// <para>
/// <see cref="ResetTo"/> exists because re-ordering through <c>Move</c> raises one change per row, and the
/// grid answers each with its own slot bookkeeping — quadratic work, on a large result, for one change of view.
/// </para>
/// <para>
/// The arrival stamps live here, in the collection's own insert/replace/remove hooks, so no caller can add or
/// drop a row without them following. They exist only while <see cref="IsStamping"/>: an unsorted buffer is in
/// arrival order already, so <see cref="StartStamping"/> derives every stamp from the position it has then, and
/// a result that is never sorted pays nothing.
/// </para>
/// </summary>
public sealed class RowCollection : ObservableCollection<object?[]>
{
    private readonly Dictionary<object?[], long> _arrival = new(ReferenceEqualityComparer.Instance);
    private long _next;

    public RowCollection(IEnumerable<object?[]> rows) : base(rows)
    {
    }

    /// <summary>Whether rows are being stamped with their arrival order — true while a sort is in force.</summary>
    public bool IsStamping { get; private set; }

    /// <summary>
    /// Begin stamping, numbering the rows by their current positions. Only correct while the buffer is still in
    /// arrival order, i.e. unsorted — which is the one moment a sort starts from.
    /// </summary>
    public void StartStamping()
    {
        if (IsStamping)
        {
            return;
        }
        _arrival.Clear();
        for (var i = 0; i < Items.Count; i++)
        {
            _arrival[Items[i]] = i;
        }
        _next = Items.Count;
        IsStamping = true;
    }

    /// <summary>Stop stamping and drop the stamps — the buffer is back in arrival order.</summary>
    public void StopStamping()
    {
        _arrival.Clear();
        IsStamping = false;
    }

    /// <summary>A row's place in the query's order. A row with no stamp sorts after every row with one.</summary>
    public long ArrivalOf(object?[] row) => _arrival.TryGetValue(row, out var stamp) ? stamp : long.MaxValue;

    /// <summary>Stamp a row that is about to be inserted, so it can be compared before it lands. A no-op for a
    /// row already stamped, or while not stamping.</summary>
    public void Stamp(object?[] row)
    {
        if (IsStamping && _arrival.TryAdd(row, _next))
        {
            _next++;
        }
    }

    /// <summary>Replace the contents with <paramref name="rows"/> — the same rows, re-ordered — raising a single reset.</summary>
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

    protected override void InsertItem(int index, object?[] item)
    {
        Stamp(item);
        base.InsertItem(index, item);
    }

    /// <summary>A saved row's array swapped for the committed one takes over its place in the query's order.</summary>
    protected override void SetItem(int index, object?[] item)
    {
        if (IsStamping && _arrival.Remove(Items[index], out var stamp))
        {
            _arrival[item] = stamp;
        }
        base.SetItem(index, item);
    }

    protected override void RemoveItem(int index)
    {
        _arrival.Remove(Items[index]);
        base.RemoveItem(index);
    }

    protected override void ClearItems()
    {
        _arrival.Clear();
        base.ClearItems();
    }
}
