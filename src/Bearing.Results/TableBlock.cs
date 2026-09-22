using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Data;

namespace Bearing.Results;

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

}
