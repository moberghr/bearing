namespace Bearing.App.Results;

/// <summary>
/// Reading and cycling a checkbox column's cell value. Two lines of logic that have to agree in two places —
/// the <c>CheckBox</c> the mouse clicks and the keyboard toggle behind <c>grid.beginEdit</c> — so they live
/// here once, pure and testable, rather than being spelled out twice (§2.5).
/// </summary>
public static class BoolCellValue
{
    /// <summary>A cell's value as a three-state bool: null for SQL NULL, an out-of-range column, or anything
    /// that isn't a bool. A pasted or freshly-typed cell can still hold the raw text, so "true"/"false" are
    /// read too (save-time coercion is what normalizes it — see <c>ResultEditModel.Coerce</c>).</summary>
    public static bool? Read(object?[]? row, int index)
    {
        if (row is null || index < 0 || index >= row.Length) return null;
        return row[index] switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => null,
        };
    }

    /// <summary>The next value in the checkbox's three-state cycle — false → true → NULL → false — matching
    /// what Avalonia's own <c>ToggleButton</c> does with <c>IsThreeState</c>, so the keyboard and the mouse
    /// walk the same ring.</summary>
    public static bool? Next(bool? current) => current switch
    {
        false => true,
        true => null,
        _ => false,
    };
}
