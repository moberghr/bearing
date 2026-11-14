using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.App.ViewModels;
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
/// <item>The query's own order is always recoverable. Every row is stamped with the order it arrived in, and
/// "clear sort" — and every tie — falls back to that, so the sort is stable and undoable. That order is often
/// what the user asked for with an <c>ORDER BY</c>.</item>
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
    /// <summary>
    /// Above this many comparisons-worth of work (incoming × loaded), a page is merged in and the buffer reset
    /// once, rather than inserted row by row. Inserting is kinder to the viewport — the grid sees each row land
    /// — but every insert shifts the buffer, which turns a fetch-all of a large sorted result quadratic.
    /// </summary>
    internal const long InsertBudget = 2_000_000;

    private readonly Dictionary<object?[], long> _arrival = new(ReferenceEqualityComparer.Instance);
    private long _next;

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

    /// <summary>Stamp a row with its arrival order. A row already stamped keeps its stamp.</summary>
    public void Arrived(object?[] row)
    {
        if (_arrival.TryAdd(row, _next))
        {
            _next++;
        }
    }

    /// <summary>A saved row replaced by its committed array takes over the old one's place in the query's order.</summary>
    public void Succeeds(object?[] oldRow, object?[] newRow)
    {
        if (_arrival.Remove(oldRow, out var stamp))
        {
            _arrival[newRow] = stamp;
        }
        else
        {
            Arrived(newRow);
        }
    }

    /// <summary>A row that has left the buffer.</summary>
    public void Forget(object?[] row) => _arrival.Remove(row);

    /// <summary>
    /// Sort <paramref name="rows"/> on <paramref name="column"/> (or back to arrival order for
    /// <see cref="SortDirection.None"/>), record it, and return the new order. The caller owns the buffer.
    /// </summary>
    public List<object?[]> Apply(int column, SortDirection direction, IEnumerable<object?[]> rows)
    {
        if (direction == SortDirection.None)
        {
            Column = null;
            Direction = SortDirection.None;
        }
        else
        {
            Column = column;
            Direction = direction;
        }

        var ordered = rows.ToList();
        ordered.Sort(Compare);   // not a stable sort, but Compare is total: ties fall to arrival order
        Changed?.Invoke();
        return ordered;
    }

    /// <summary>
    /// Put newly loaded rows into <paramref name="buffer"/> where the current sort says they belong — at the
    /// end when unsorted. Each row must already be <see cref="Arrived"/>.
    /// <para>
    /// "Where they belong" is a binary search over the buffer as it stands. Rows the user has edited or added
    /// since the sort are where the sort left them, not where their new values would put them — the same as a
    /// spreadsheet — so the buffer is only nearly sorted and a newcomer lands next to its nearest sorted
    /// neighbours. Nothing is re-ordered to make room.
    /// </para>
    /// </summary>
    public void Place(RowCollection buffer, IReadOnlyList<object?[]> incoming)
    {
        if (!IsSorted)
        {
            foreach (var row in incoming)
            {
                buffer.Add(row);
            }
            return;
        }

        if ((long)incoming.Count * buffer.Count <= InsertBudget)
        {
            foreach (var row in incoming)
            {
                buffer.Insert(InsertionIndex(buffer, row), row);
            }
            return;
        }

        buffer.ResetTo(Merge(buffer, incoming));
    }

    /// <summary>
    /// The status line after sorting a result that is only partly fetched, or null when every row is loaded
    /// or the rows are back in the query's order. Sorting 200 of 50,000 rows and presenting it as sorted would
    /// be a lie, so it is said, in the words a part-loaded column selection already uses.
    /// </summary>
    public static string? PartialNotice(ResultSetViewModel result)
    {
        if (result is not { IsPageable: true, HasMore: true } || result.Sort.Column is not { } column)
        {
            return null;
        }
        var name = column < result.Columns.Count ? result.Columns[column].Name : "column";
        var way = result.Sort.Direction == SortDirection.Descending ? "descending" : "ascending";
        return $"Sorted by “{name}” {way} over the loaded rows only ({result.RowCountText}) — ⤓ all fetches the rest.";
    }

    /// <summary>Order two rows by the current sort, then by arrival.</summary>
    public int Compare(object?[] a, object?[] b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }
        if (Column is { } column)
        {
            var byValue = CompareValues(Cell(a, column), Cell(b, column));
            if (byValue != 0)
            {
                return Direction == SortDirection.Descending ? -byValue : byValue;
            }
        }
        return ArrivalOf(a).CompareTo(ArrivalOf(b));
    }

    /// <summary>
    /// Ascending order of two cell values: NULLs last; numbers by value across CLR types (an <c>int4</c> and a
    /// <c>numeric</c> compare as numbers); like-typed comparables by their own order; everything else by its
    /// displayed text. Never throws — an edited cell holds the user's raw text in a numeric column, and a sort
    /// must still finish.
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

        if (IsNumber(a) && IsNumber(b))
        {
            return CompareNumbers(a, b);
        }
        if (a is string sa && b is string sb)
        {
            return CompareText(sa, sb);
        }
        if (a is byte[] ba && b is byte[] bb)
        {
            return ba.AsSpan().SequenceCompareTo(bb);
        }
        if (a.GetType() == b.GetType() && a is IComparable comparable)
        {
            try
            {
                return comparable.CompareTo(b);
            }
            catch (ArgumentException)
            {
                // A comparable that refuses its own type: fall through to the text.
            }
        }
        return CompareText(CellFormat.Display(a), CellFormat.Display(b));
    }

    private int InsertionIndex(IReadOnlyList<object?[]> buffer, object?[] row)
    {
        // Upper bound: after every row that sorts at or before this one. A newcomer always arrived last, so
        // among equals it belongs after them.
        int lo = 0, hi = buffer.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (Compare(buffer[mid], row) <= 0)
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

    private List<object?[]> Merge(IReadOnlyList<object?[]> buffer, IReadOnlyList<object?[]> incoming)
    {
        var sorted = incoming.ToList();
        sorted.Sort(Compare);
        var merged = new List<object?[]>(buffer.Count + sorted.Count);
        int i = 0, j = 0;
        while (i < buffer.Count && j < sorted.Count)
        {
            merged.Add(Compare(buffer[i], sorted[j]) <= 0 ? buffer[i++] : sorted[j++]);
        }
        while (i < buffer.Count)
        {
            merged.Add(buffer[i++]);
        }
        while (j < sorted.Count)
        {
            merged.Add(sorted[j++]);
        }
        return merged;
    }

    // A row nobody stamped (should not happen) sorts after everything that was.
    private long ArrivalOf(object?[] row) => _arrival.TryGetValue(row, out var stamp) ? stamp : long.MaxValue;

    private static object? Cell(object?[] row, int column) => column < row.Length ? row[column] : null;

    private static int CompareText(string a, string b)
    {
        var folded = string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        return folded != 0 ? folded : string.CompareOrdinal(a, b);
    }

    private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint
        or long or ulong or decimal or float or double;

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
}
