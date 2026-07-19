using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Squirrel.App.Results;

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

    /// <summary>Format a stat number: rounded to ≤2 dp, locale grouped (e.g. 1,234.5).</summary>
    public static string Format(double value) => value.ToString("#,##0.##", CultureInfo.CurrentCulture);
}
