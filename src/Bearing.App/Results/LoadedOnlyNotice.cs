using Bearing.App.ViewModels;

namespace Bearing.App.Results;

/// <summary>
/// The one sentence that says an action covered only the rows already loaded, on a result that has more to
/// fetch: "Column selected over the loaded rows only (200 rows) — “all” fetches the rest." Selecting a column
/// (#6) and sorting (#106) both act on the loaded rows, and on a part-fetched result both must say so —
/// sorting 200 of 50,000 rows and presenting it as sorted would be a lie. One builder, so the two never drift.
/// </summary>
public static class LoadedOnlyNotice
{
    /// <summary>The notice for <paramref name="what"/> ("Column selected"), or null when every row is
    /// loaded and there is nothing to warn about.</summary>
    public static string? For(ResultSetViewModel result, string what)
        => result is { IsPageable: true, HasMore: true }
            ? $"{what} over the loaded rows only ({result.RowCountText}) — “all” fetches the rest."
            : null;

    /// <summary>"Sorted by “name” ascending", the subject of a sort's notice.</summary>
    public static string Sorted(ResultSetViewModel result, RowSort sort)
    {
        var name = sort.Column >= 0 && sort.Column < result.Columns.Count ? result.Columns[sort.Column].Name : "column";
        var way = sort.Direction == SortDirection.Ascending ? "ascending" : "descending";
        return $"Sorted by “{name}” {way}";
    }
}
