using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.App.Formatting;

namespace Bearing.App.Results;

/// <summary>Aggregate of a numeric cell selection (design RESULTS_GRID §7).</summary>
public readonly record struct CellStatistics(int Count, double Sum, double Avg, double Min, double Max);

/// <summary>
/// Pure helpers for the quick-stats feature: which columns are numeric "measures" (selectable for
/// stats), parsing cell values to numbers, and aggregating a selection. No UI dependencies.
/// </summary>
public static class CellStats
{
    private static readonly HashSet<Type> NumericTypes = new()
    {
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(decimal),
    };

    /// <summary>True for numeric CLR types (unwrapping Nullable&lt;T&gt;).</summary>
    public static bool IsNumeric(Type type)
        => NumericTypes.Contains(Nullable.GetUnderlyingType(type) ?? type);

    /// <summary>
    /// A column is a selectable "measure" when it's numeric AND not a primary key AND not a foreign key —
    /// summing identifiers is meaningless, so IDs/FKs are deliberately excluded.
    /// </summary>
    public static bool IsMeasureColumn(Type clrType, bool isPrimaryKey, bool isForeignKey)
        => IsNumeric(clrType) && !isPrimaryKey && !isForeignKey;

    /// <summary>Parse a raw cell value (boxed number or its text form) to a double.</summary>
    public static bool TryParseNumber(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d: result = d; return true;
            case float f: result = f; return true;
            case decimal m: result = (double)m; return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            case string s:
                return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out result)
                    || double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>Aggregate the parseable numbers in a selection; null when none parse.</summary>
    public static CellStatistics? Aggregate(IEnumerable<object?> values)
    {
        var nums = new List<double>();
        foreach (var v in values)
            if (TryParseNumber(v, out var d)) nums.Add(d);
        if (nums.Count == 0) return null;
        return new CellStatistics(nums.Count, nums.Sum(), nums.Average(), nums.Min(), nums.Max());
    }

    /// <summary>
    /// Format a stat number: rounded to ≤2 dp, grouped the same way the cells above it are.
    /// <para>
    /// Both halves used to be the machine's culture (<c>"#,##0.##"</c> over <c>CurrentCulture</c>), which put
    /// the bar in a different convention from the grid it summarises: on a comma-decimal locale it read
    /// <c>1.234,5</c> directly under cells reading <c>1,234.5</c>. It also grouped when the user had turned
    /// grouping off. Going through <c>NumberGrouping</c> answers both — one convention per view, and one
    /// switch.
    /// </para>
    /// <para>
    /// Grouping is safe here in a way it is not in a cell: this is a computed aggregate that is never copied
    /// back into a cell, re-parsed, or written anywhere. Nothing round-trips it.
    /// </para>
    /// </summary>
    public static string Format(double value)
        => NumberGrouping.Apply(value.ToString("0.##", CultureInfo.InvariantCulture));
}
