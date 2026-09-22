using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Results;

namespace Bearing.App.Results;

/// <summary>
/// The half of <see cref="TableBlock"/> and <see cref="ResultExport"/> that needs a view model: building a
/// block from a live result or a grid selection, and the names an export suggests.
/// <para>
/// Left behind when the rest moved to <c>Bearing.Results</c> so two hosts could share the writers. What is
/// here reads a <see cref="ResultSetViewModel"/>, which only exists where there is a grid — a command-line
/// export has a result, no tab to name a file after, and no selection at all.
/// </para>
/// </summary>
public static class ResultBlocks
{
    /// <summary>Every loaded row of a result set, all columns. Rows are shared, not copied — the caller is
    /// expected to have snapshotted them if it is going to format off the UI thread.</summary>
    public static TableBlock ForResult(ResultSetViewModel result)
        => result.Columns.Count == 0 ? TableBlock.Empty : new TableBlock(result.Columns, result.Rows.ToList());

    /// <summary>
    /// The selection's bounding rectangle. A Ctrl-click gap is <b>filled</b> with its real value rather than
    /// blanked, unlike <see cref="GridSelectionOps.Tsv"/>: TSV's blanks exist to keep a spreadsheet paste
    /// aligned with what was selected, whereas CSV/JSON/SQL/… describe data, where a ragged hole would either
    /// misalign every following field or invent a NULL that isn't in the database.
    /// </summary>
    public static TableBlock ForSelection(
        ResultSetViewModel result, IReadOnlyCollection<(object?[] Row, int Col)> cells)
    {
        if (cells.Count == 0) return TableBlock.Empty;
        var rows = result.Rows;
        var rowIdx = cells.Select(c => rows.IndexOf(c.Row)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        var colIdx = cells.Select(c => c.Col).Where(c => c < result.Columns.Count).Distinct().OrderBy(c => c).ToList();
        if (rowIdx.Count == 0 || colIdx.Count == 0) return TableBlock.Empty;

        var columns = colIdx.Select(c => result.Columns[c]).ToList();
        var projected = rowIdx
            .Select(ri =>
            {
                var row = rows[ri];
                return colIdx.Select(c => c < row.Length ? row[c] : null).ToArray();
            })
            .ToList();
        return new TableBlock(columns, projected);
    }

    /// <summary>
    /// A default file name for a result: the table it came from when that's known, else the tab name, else
    /// "result" — with a timestamp, since exporting the same query twice is the normal case and silently
    /// overwriting yesterday's file is not what anyone means by Export.
    /// </summary>
    public static string SuggestedName(ResultSetViewModel result, string? tabName, DateTime now, ExportFormat format)
    {
        var stem = result.EditTarget?.Table
            ?? (string.IsNullOrWhiteSpace(tabName) ? null : tabName)
            ?? "result";
        return $"{ResultExport.Slug(stem)}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.{ResultExport.Extension(format)}";
    }

    /// <summary>The Excel sheet name for a result — the source table when known, else "Result".</summary>
    public static string SheetName(ResultSetViewModel result)
        => XlsxWriter.SafeSheetName(result.EditTarget?.Table ?? "Result");

    /// <summary>
    /// The sheets a run's workbook should hold, in the order the user sees them: the grid results only.
    /// <para>
    /// A statement message ("UPDATE 3") and an error are not sheets — an empty tab named after a DELETE would
    /// be worse than its absence. Result numbers come from the position in the <i>run</i>, not in the filtered
    /// list, so "Result 3" in the workbook is the third result on screen even when the second was a message.
    /// </para>
    /// </summary>
    public static IReadOnlyList<XlsxWriter.Sheet> RunSheets(IReadOnlyList<ResultSetViewModel> results)
    {
        var sheets = new List<XlsxWriter.Sheet>();
        for (var i = 0; i < results.Count; i++)
        {
            if (!results[i].HasGrid) continue;
            var named = results[i].EditTarget?.Table is { } table
                ? XlsxWriter.SafeSheetName(table)
                : $"Result {i + 1}";
            sheets.Add(new XlsxWriter.Sheet(ForResult(results[i]), named));
        }
        return sheets;
    }
}
