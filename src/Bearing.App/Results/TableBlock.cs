using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Core.Data;

namespace Bearing.App.Results;

/// <summary>
/// A rectangular block of result data: the columns in output order, and the rows projected onto them.
/// Every copy-as and export formatter consumes this, so a cell selection and a whole result set format
/// through exactly the same code — the only difference is which factory built the block.
/// <para>
/// Pure data: no grid, no clipboard, no files. That is what makes the formats testable at all (§2.5 —
/// Wayland blocks driving the grid, §4.3).
/// </para>
/// </summary>
public sealed record TableBlock(IReadOnlyList<ColumnDescriptor> Columns, IReadOnlyList<object?[]> Rows)
{
    public static TableBlock Empty { get; } = new(Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>());

    public bool IsEmpty => Columns.Count == 0 || Rows.Count == 0;

    /// <summary>A cell's raw value, tolerating a row shorter than the column list (a pending-new row is
    /// created at the result's width, but a projected row can still be short if columns changed).</summary>
    public object? Value(int rowIndex, int columnIndex)
    {
        var row = Rows[rowIndex];
        return columnIndex < row.Length ? row[columnIndex] : null;
    }

    /// <summary>Every loaded row of a result set, all columns. Rows are shared, not copied — the caller is
    /// expected to have snapshotted them if it is going to format off the UI thread.</summary>
    public static TableBlock ForResult(ResultSetViewModel result)
        => result.Columns.Count == 0 ? Empty : new TableBlock(result.Columns, result.Rows.ToList());

    /// <summary>
    /// The selection's bounding rectangle. A Ctrl-click gap is <b>filled</b> with its real value rather than
    /// blanked, unlike <see cref="GridSelectionOps.Tsv"/>: TSV's blanks exist to keep a spreadsheet paste
    /// aligned with what was selected, whereas CSV/JSON/SQL/… describe data, where a ragged hole would either
    /// misalign every following field or invent a NULL that isn't in the database.
    /// </summary>
    public static TableBlock ForSelection(
        ResultSetViewModel result, IReadOnlyCollection<(object?[] Row, int Col)> cells)
    {
        if (cells.Count == 0) return Empty;
        var rows = result.Rows;
        var rowIdx = cells.Select(c => rows.IndexOf(c.Row)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        var colIdx = cells.Select(c => c.Col).Where(c => c < result.Columns.Count).Distinct().OrderBy(c => c).ToList();
        if (rowIdx.Count == 0 || colIdx.Count == 0) return Empty;

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
}
