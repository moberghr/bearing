using System;
using Bearing.Core.Data;

namespace Bearing.App.Results;

/// <summary>
/// Column-shape predicates the results grid branches on when it decides how to render and select a
/// column. They were private statics duplicated across the <c>ResultView</c> partials, which is how
/// "is this a bool column?" ended up being asked in five places by three different spellings.
/// </summary>
public static class ColumnKinds
{
    /// <summary>A bool (or nullable bool) column — rendered as a checkbox instead of text. It selects like any
    /// other cell; what it lacks is a text editor, so editing it cycles the value in place
    /// (<c>GridSelectionController.ToggleBool</c>, reached by double-tap or Space/Enter/F2), skipping NULL when
    /// the column is NOT NULL.</summary>
    public static bool IsBool(ColumnDescriptor c)
        => (Nullable.GetUnderlyingType(c.ClrType) ?? c.ClrType) == typeof(bool);

    /// <summary>A Postgres json / jsonb column — gets a type badge and always offers the inspector.</summary>
    public static bool IsJson(string dataTypeName)
        => string.Equals(dataTypeName, "jsonb", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataTypeName, "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A <c>timestamp without time zone</c> column (#77) — it gets a badge saying so.
    /// <para>
    /// The mark matters because the absence of an offset is too weak a signal on its own: a bare
    /// <c>2026-08-26 12:15:00</c> reads as "the zone got truncated", not as "this value has no zone and never
    /// did". That ambiguity is the reported problem, and it survives fixing <c>timestamptz</c> unless the
    /// zone-less case is marked.
    /// </para>
    /// </summary>
    public static bool IsTimestampWithoutZone(string dataTypeName)
        => dataTypeName.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)
        && !dataTypeName.Contains("with time zone", StringComparison.OrdinalIgnoreCase)
        && !dataTypeName.EndsWith("tz", StringComparison.OrdinalIgnoreCase);

    /// <summary>A <c>timestamptz</c> column: its values are real instants, so the display zone applies.</summary>
    public static bool IsTimestampWithZone(string dataTypeName)
        => dataTypeName.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)
        && !IsTimestampWithoutZone(dataTypeName);

    /// <summary>Whether a value's text looks like JSON, for columns not declared json/jsonb (a text column
    /// holding a serialized document still deserves the tree view).</summary>
    public static bool LooksJson(string raw)
    {
        var t = raw.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }
}
