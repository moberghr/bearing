using System;
using System.Collections.Generic;
using System.Globalization;
using Bearing.Results;

namespace Bearing.App.Results;

/// <summary>Which way a result is sorted on a column. <see cref="None"/> is the order the query produced.</summary>
public enum SortDirection
{
    None,
    Ascending,
    Descending,
}

/// <summary>
/// Client-side sort of a result's <b>loaded</b> rows (#106) — the state behind the header's sort glyph and
/// the column menu's Sort items.
/// <para>
/// Per result set, like <see cref="ColumnLayout"/>, and pure for the same reason (§2.5): the rules that are
/// easy to get wrong are tested here without a window.
/// </para>
/// <list type="bullet">
/// <item>The query's own order is always recoverable. The buffer stamps every row with the order it arrived in
/// (<see cref="RowCollection"/>), and "clear sort" — and every tie — falls back to that, so the sort is stable
/// and undoable. That order is often what the user asked for with an <c>ORDER BY</c>.</item>
/// <item>Rows are tracked by reference, as the pending edits are (<c>ResultSetViewModel</c>), so a sort never
/// detaches an edit or a selection from its row.</item>
/// <item>NULLs sort last ascending and first descending — what Postgres does by default — so a sort here
/// agrees with the same <c>ORDER BY</c> run on the server, at least as far as NULLs go.</item>
/// <item>Text compares culture-invariantly, case-insensitively: a stable answer on every machine, which is
/// not necessarily the server's collation. This sorts what is on screen; it does not claim to be the
/// database's order.</item>
/// </list>
/// </summary>
public sealed class ResultSort
{
    private readonly RowCollection _rows;

    public ResultSort(RowCollection rows) => _rows = rows;

    /// <summary>The sorted column (source index), or null when the rows are in the query's order.</summary>
    public int? Column { get; private set; }

    /// <summary>The direction of <see cref="Column"/>; <see cref="SortDirection.None"/> when unsorted.</summary>
    public SortDirection Direction { get; private set; }

    public bool IsSorted => Column is not null;

    /// <summary>Raised whenever the sort changes, so the header glyphs can re-read it.</summary>
    public event Action? Changed;

    /// <summary>How many handlers are attached — see <see cref="ColumnLayout.ChangedSubscribers"/> for why a
    /// leak here needs to be assertable.</summary>
    internal int ChangedSubscribers => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>The direction <paramref name="column"/> is sorted in; None for every other column.</summary>
    public SortDirection DirectionOf(int column) => Column == column ? Direction : SortDirection.None;

    /// <summary>What one click on the glyph does next: ascending, then descending, then back to the query's order.</summary>
    public static SortDirection Next(SortDirection current) => current switch
    {
        SortDirection.None => SortDirection.Ascending,
        SortDirection.Ascending => SortDirection.Descending,
        _ => SortDirection.None,
    };

    /// <summary>
    /// Sort the buffer on <paramref name="column"/>, or back to arrival order for <see cref="SortDirection.None"/>,
    /// in one reset.
    /// </summary>
    public void Apply(int column, SortDirection direction)
    {
        _rows.StartStamping();   // before Column changes: from unsorted, positions are still the arrival order
        Column = direction == SortDirection.None ? null : column;
        Direction = direction;
        Reapply();
        if (!IsSorted)
        {
            _rows.StopStamping();
        }
        Changed?.Invoke();
    }

    /// <summary>Re-sort the whole buffer by the sort in force — after a bulk load that appended out of order.</summary>
    public void Reapply()
    {
        if (!_rows.IsStamping)
        {
            return;   // never sorted: the buffer is already in arrival order
        }

        // Keyed once per row, so the comparison reads no dictionary and no row array.
        var keyed = new Keyed[_rows.Count];
        for (var i = 0; i < keyed.Length; i++)
        {
            var row = _rows[i];
            keyed[i] = new Keyed(row, Column is { } c ? Cell(row, c) : null, _rows.ArrivalOf(row));
        }
        var sign = Direction == SortDirection.Descending ? -1 : 1;
        var byValue = IsSorted;
        Array.Sort(keyed, (a, b) =>
        {
            var v = byValue ? CompareValues(a.Value, b.Value) * sign : 0;
            return v != 0 ? v : a.Arrival.CompareTo(b.Arrival);
        });

        var ordered = new List<object?[]>(keyed.Length);
        foreach (var k in keyed)
        {
            ordered.Add(k.Row);
        }
        _rows.ResetTo(ordered);
    }

    /// <summary>
    /// Put newly loaded rows where the current sort says they belong — at the end when unsorted.
    /// <para>
    /// "Where they belong" is a binary search over the buffer as it stands. Rows the user has edited or added
    /// since the sort are where the sort left them, not where their new values would put them — the same as a
    /// spreadsheet — so the buffer is only nearly sorted and a newcomer lands next to its nearest sorted
    /// neighbours. A page is a few hundred rows; a bulk load (fetch-all) appends instead and calls
    /// <see cref="Reapply"/> once at the end.
    /// </para>
    /// </summary>
    public void Place(IReadOnlyList<object?[]> incoming)
    {
        foreach (var row in incoming)
        {
            if (!IsSorted)
            {
                _rows.Add(row);
                continue;
            }
            _rows.Stamp(row);
            _rows.Insert(InsertionIndex(row), row);
        }
    }

    /// <summary>
    /// Ascending order of two cell values: NULLs last; like-typed comparables by their own order (text
    /// case-insensitively); numbers by value across CLR types (an <c>int4</c> and a <c>numeric</c> compare as
    /// numbers); everything else by its displayed text. Never throws — an edited cell holds the user's raw text in
    /// a numeric column, and a sort must still finish.
    /// </summary>
    public static int CompareValues(object? a, object? b)
    {
        if (a is DBNull)
        {
            a = null;
        }
        if (b is DBNull)
        {
            b = null;
        }
        if (a is null)
        {
            return b is null ? 0 : 1;
        }
        if (b is null)
        {
            return -1;
        }

        var type = a.GetType();
        if (type == b.GetType())
        {
            switch (a)
            {
                case string sa:
                    return CompareText(sa, (string)b);
                case byte[] ba:
                    return ba.AsSpan().SequenceCompareTo((byte[])b);
                case IComparable comparable:
                    try
                    {
                        return comparable.CompareTo(b);
                    }
                    catch (ArgumentException)
                    {
                        break;   // a comparable that refuses its own type: fall through to the text
                    }
            }
        }
        else if (CellFormat.IsNumeric(type) && CellFormat.IsNumeric(b.GetType()))
        {
            return CompareNumbers(a, b);
        }
        return CompareText(CellFormat.Display(a), CellFormat.Display(b));
    }

    private int Compare(object?[] a, object?[] b)
    {
        if (Column is { } column)
        {
            var byValue = CompareValues(Cell(a, column), Cell(b, column));
            if (byValue != 0)
            {
                return Direction == SortDirection.Descending ? -byValue : byValue;
            }
        }
        return _rows.ArrivalOf(a).CompareTo(_rows.ArrivalOf(b));
    }

    private int InsertionIndex(object?[] row)
    {
        // Upper bound: after every row that sorts at or before this one. A newcomer always arrived last, so
        // among equals it belongs after them.
        int lo = 0, hi = _rows.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (Compare(_rows[mid], row) <= 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private static object? Cell(object?[] row, int column) => column < row.Length ? row[column] : null;

    private static int CompareText(string a, string b)
    {
        var folded = string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        return folded != 0 ? folded : string.CompareOrdinal(a, b);
    }

    private static int CompareNumbers(object a, object b)
    {
        if (a is double or float || b is double or float)
        {
            return Convert.ToDouble(a, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
        }
        return Convert.ToDecimal(a, CultureInfo.InvariantCulture)
            .CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
    }

    private readonly record struct Keyed(object?[] Row, object? Value, long Arrival);
}
