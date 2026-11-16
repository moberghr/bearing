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
/// <item><b>Sort</b>: one column; nulls last in either direction; stable, so ties keep load order.</item>
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
            // OrderBy is stable, which is the "ties keep load order" rule. Nulls are partitioned out first so
            // they land last whichever way the rest is ordered.
            var withValue = loaded.Where(r => Value(r, sort.Column) is not null);
            var nulls = loaded.Where(r => Value(r, sort.Column) is null);
            var comparer = Comparer<object?>.Create(CompareValues);
            var ordered = sort.Direction == SortDirection.Ascending
                ? withValue.OrderBy(r => Value(r, sort.Column), comparer)
                : withValue.OrderByDescending(r => Value(r, sort.Column), comparer);
            loaded = ordered.Concat(nulls).ToList();
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
    /// Order two non-null values: by their own comparison when they are the same comparable type (numbers,
    /// dates, booleans), else numerically when both read as numbers, else culture-aware and case-insensitive
    /// over their text.
    /// </summary>
    public static int CompareValues(object? a, object? b)
    {
        if (a is null || b is null) return a is null ? (b is null ? 0 : 1) : -1;
        // Strings skip their own CompareTo, which is case-sensitive: "10" vs "9" should read as numbers, and
        // "a" vs "A" should tie (and keep load order) rather than split on case.
        if (a is not string && a.GetType() == b.GetType() && a is IComparable ca) return ca.CompareTo(b);
        if (ColumnFilter.TryNumber(a, out var x) && ColumnFilter.TryNumber(b, out var y)) return x.CompareTo(y);
        return string.Compare(Text(a), Text(b), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
    }

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
