using Bearing.App.ViewModels;

namespace Bearing.App.Results;

/// <summary>
/// The strings that label a result set — the meta row's caption and a tab's header. Pure formatting, pulled
/// out of <c>ResultView</c> so the three-way shape (error / no-columns statement / row-bearing grid) can be
/// asserted without building a window.
/// </summary>
public static class ResultMetaText
{
    /// <summary>Meta-row caption: "Result · 10 rows · 88 ms", or the message / error for a non-grid result.
    /// Used for results whose row count is fixed; pageable grids bind the live
    /// <see cref="ResultSetViewModel.MetaDetail"/> after a static "<paramref name="label"/> · " prefix so
    /// the count tracks infinite-scroll loads.</summary>
    public static string Meta(string? label, ResultSetViewModel result)
    {
        var name = label ?? "Result";
        if (!result.Success) return $"{name} · error: {result.Error?.Message}";
        if (result.Columns.Count == 0) return $"{name} · {result.Message ?? "Statement executed."}";
        var ms = (long)System.Math.Round(result.Duration.TotalMilliseconds);
        var rows = result.RowCount == 1 ? "1 row" : $"{result.RowCount} rows";
        return $"{name} · {rows} · {ms} ms";
    }

    /// <summary>A tab header for result <paramref name="index"/> (0-based) in tabbed view.</summary>
    public static string TabHeader(int index, ResultSetViewModel result)
    {
        if (!result.Success) return $"Result {index + 1} · error";
        if (result.Columns.Count == 0) return $"Result {index + 1} · {result.Message}";
        return $"Result {index + 1} ({result.RowCount})";
    }

    /// <summary>The cell inspector's header, e.g. <c>film[42].description</c> — the edit target's table (or
    /// "row" when the result isn't attributable to one) keyed by the row's first non-null primary key.</summary>
    public static string InspectorTitle(ResultSetViewModel result, int index, object?[] row)
        => $"{result.EditTarget?.Table ?? "row"}[{KeyDisplay(result, row)}].{result.Columns[index].Name}";

    /// <summary>The row's first non-null primary-key value, or "?" when it has none to identify it by.</summary>
    private static string KeyDisplay(ResultSetViewModel result, object?[] row)
    {
        foreach (var i in result.PrimaryKeyColumns)
            if (i < row.Length && row[i] is not null) return Formatting.CellFormat.Display(row[i]);
        return "?";
    }
}
