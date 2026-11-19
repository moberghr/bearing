using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.Core.Data;

namespace Bearing.App.Results;

/// <summary>Which way a client-side sort runs (#106).</summary>
public enum SortDirection { Ascending, Descending }

/// <summary>A one-column client-side sort: the result column it orders by, and which way.</summary>
public sealed record RowSort(int Column, SortDirection Direction);

/// <summary>
/// The rows a result shows once its client-side filter and sort are applied (#106, design v4). Pure, so the
/// rules below are unit-tested without a grid (§2.5).
/// <list type="bullet">
/// <item><b>Loaded rows only.</b> Nothing is re-queried; the caller says so on screen.</item>
/// <item><b>Quick filter</b>: case-insensitive substring over every column's text (json as its text).</item>
/// <item><b>Column filters</b> (<see cref="ColumnFilter"/>), AND-ed with each other and the quick filter.</item>
/// <item><b>Sort</b>: one column; nulls last ascending and first descending (Postgres's default); stable, so
/// ties keep load order.</item>
/// <item><b>New unsaved rows</b> are always shown, pinned at the bottom in insertion order — a filter never
/// hides a pending insert, and a sort never moves one.</item>
/// </list>
/// </summary>
public static class RowView
{
    /// <summary>The visible rows, in view order.</summary>
    public static List<object?[]> Apply(
        IReadOnlyList<object?[]> rows,
        Func<object?[], bool> isNew,
        string? quickFilter,
        IReadOnlyDictionary<int, string> columnFilters,
        RowSort? sort)
    {
        var quick = string.IsNullOrWhiteSpace(quickFilter) ? null : quickFilter.Trim();
        var filters = columnFilters
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => (Column: f.Key, Filter: ColumnFilter.Parse(f.Value)))
            .ToList();

        var loaded = new List<object?[]>(rows.Count);
        var pinned = new List<object?[]>();
        foreach (var row in rows)
        {
            if (isNew(row)) { pinned.Add(row); continue; }
            if (quick is not null && !MatchesQuick(row, quick)) continue;
            if (!filters.All(f => f.Filter.Matches(Value(row, f.Column)))) continue;
            loaded.Add(row);
        }

        if (sort is not null)
        {
            // OrderBy is stable, which is the "ties keep load order" rule. Nulls are partitioned out first and
            // placed the way Postgres places them by default — last ascending, first descending — so the local
            // order matches the same ORDER BY run on the server, nulls included.
            var withValue = loaded.Where(r => Value(r, sort.Column) is not null);
            var nulls = loaded.Where(r => Value(r, sort.Column) is null);
            var comparer = Comparer<object?>.Create(CompareValues);
            loaded = sort.Direction == SortDirection.Ascending
                ? withValue.OrderBy(r => Value(r, sort.Column), comparer).Concat(nulls).ToList()
                : nulls.Concat(withValue.OrderByDescending(r => Value(r, sort.Column), comparer)).ToList();
        }

        loaded.AddRange(pinned);
        return loaded;
    }

    /// <summary>Whether a column can be sorted. Documents (json, jsonb, xml) can't — there is no order worth
    /// having over them; they can be filtered instead.</summary>
    public static bool IsSortable(ColumnDescriptor column) => !ColumnKinds.IsDocument(column.DataTypeName);

    /// <summary>What a header click on the sort indicator does next: ascending → descending → off, and a
    /// different column starts again at ascending.</summary>
    public static RowSort? Next(RowSort? current, int column)
        => current is null || current.Column != column ? new RowSort(column, SortDirection.Ascending)
         : current.Direction == SortDirection.Ascending ? current with { Direction = SortDirection.Descending }
         : null;

    /// <summary>
    /// Order two values. A <b>total</b> order, so a sort's output is the same every time — it must be, since
    /// one column can hold numbers, number-like text and plain text at once (an edited cell, a varchar of
    /// codes). Comparing pair by pair — numerically when both sides read as numbers, else as text — is not
    /// transitive: 9 &lt; 10 as numbers, "10" &lt; "1a" and "1a" &lt; "9" as text, a cycle that leaves the sort
    /// arbitrary. So each value is first put in a class, and classes never interleave:
    /// <list type="number">
    /// <item><b>Numbers</b> — a numeric CLR value, or text that reads as an invariant number — by value.</item>
    /// <item><b>Other comparable values</b> (dates, booleans, GUIDs, a double too large for a decimal) by their
    /// own comparison; two different types are kept apart by type name.</item>
    /// <item><b>Text</b> and everything else, by its text: invariant, so every machine sorts the same, and
    /// case-insensitive, so "a" and "A" tie and keep load order.</item>
    /// </list>
    /// Null sorts after everything; <see cref="Apply"/> places nulls itself.
    /// </summary>
    public static int CompareValues(object? a, object? b)
    {
        if (a is null || b is null) return a is null ? (b is null ? 0 : 1) : -1;
        var (ka, kb) = (Kind(a), Kind(b));
        if (ka != kb) return ka.CompareTo(kb);
        switch (ka)
        {
            case ValueKind.Number:
                ColumnFilter.TryNumber(a, out var x);
                ColumnFilter.TryNumber(b, out var y);
                return x.CompareTo(y);
            case ValueKind.Comparable:
                return a.GetType() == b.GetType()
                    ? ((IComparable)a).CompareTo(b)
                    : string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
            default:
                return string.Compare(Text(a), Text(b), CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        }
    }

    private enum ValueKind { Number, Comparable, Text }

    private static ValueKind Kind(object value)
        => ColumnFilter.TryNumber(value, out _) ? ValueKind.Number
         : value is not string and IComparable ? ValueKind.Comparable
         : ValueKind.Text;

    private static bool MatchesQuick(object?[] row, string text)
    {
        for (var i = 0; i < row.Length; i++)
            if (row[i] is { } v && Text(v).Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static object? Value(object?[] row, int column)
        => column >= 0 && column < row.Length ? row[column] : null;

    /// <summary>A value's text as the grid's copy shows it — the raw invariant form, never grouped digits.</summary>
    internal static string Text(object value) => GridSelectionOps.CellText([value], 0);
}

/// <summary>
/// One column's filter expression (design v4 §3). Plain text is a case-insensitive <i>contains</i>. A leading
/// <c>=</c>, <c>!=</c>, <c>&gt;</c>, <c>&lt;</c>, <c>&gt;=</c> or <c>&lt;=</c> compares numerically when both sides
/// are numbers; <c>=</c> and <c>!=</c> fall back to case-insensitive equality of the text; any other operator
/// against a non-number matches nothing. A NULL cell matches no filter, <c>!=</c> included — the way SQL reads it,
/// so the push-to-server query (§5, later) can say the same thing as the local filter.
/// </summary>
public sealed class ColumnFilter
{
    private readonly string _op;
    private readonly string _operand;
    private readonly decimal? _number;

    private ColumnFilter(string op, string operand)
    {
        _op = op;
        _operand = operand;
        _number = TryNumber(operand, out var n) ? n : null;
    }

    private static readonly string[] Operators = [">=", "<=", "!=", "=", ">", "<"];

    public static ColumnFilter Parse(string expression)
    {
        var text = expression.Trim();
        foreach (var op in Operators)
            if (text.StartsWith(op, StringComparison.Ordinal))
                return new ColumnFilter(op, text[op.Length..].Trim());
        return new ColumnFilter("", text);
    }

    public bool Matches(object? value)
    {
        if (_op == "") return value is not null && RowView.Text(value).Contains(_operand, StringComparison.OrdinalIgnoreCase);
        if (value is null) return false;

        if (_number is { } right && TryNumber(value, out var left))
        {
            return _op switch
            {
                "=" => left == right,
                "!=" => left != right,
                ">" => left > right,
                "<" => left < right,
                ">=" => left >= right,
                _ => left <= right,
            };
        }
        var equal = string.Equals(RowView.Text(value), _operand, StringComparison.OrdinalIgnoreCase);
        return _op switch { "=" => equal, "!=" => !equal, _ => false };
    }

    /// <summary>A value as a number when it is one — a numeric CLR value, or text that parses as an invariant
    /// number. Not a date, not a boolean.</summary>
    internal static bool TryNumber(object value, out decimal number)
    {
        switch (value)
        {
            case decimal d: number = d; return true;
            case int or long or short or byte or sbyte or uint or ulong or ushort:
                number = Convert.ToDecimal(value, CultureInfo.InvariantCulture); return true;
            case double or float:
                var dbl = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(dbl) && Math.Abs(dbl) < 7.9e28) { number = (decimal)dbl; return true; }
                break;
            case string s:
                return decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }
        number = 0;
        return false;
    }
}
