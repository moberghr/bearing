using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Formatting;
using Bearing.App.ViewModels;

namespace Bearing.App.Results;

/// <summary>Spatial cell-cursor motion in a results grid. Deliberately not <c>Avalonia.Input.Key</c>: the
/// controller maps keystrokes onto these so the motion arithmetic stays toolkit-free and testable.</summary>
public enum GridMotion { Left, Right, Up, Down, Home, End, PageUp, PageDown }

/// <summary>
/// The arithmetic behind results-grid cell selection: where a motion lands, which cells a rectangle covers,
/// and what a selection copies as. Pure over
/// <see cref="ResultSetViewModel"/> — no controls, no clipboard, no scrolling — so the spreadsheet
/// behaviour that used to be welded into <c>ResultView</c>'s key handler can actually be asserted
/// (Wayland blocks headless keystroke tests; see §4.3).
/// <para>
/// Every column takes a selection, checkbox (bool) columns included: their cells carry the same selection
/// border as any other, so the cursor can land on one without disappearing (#9). Nothing here is
/// column-kind aware any more.
/// </para>
/// </summary>
public static class GridSelectionOps
{
    /// <summary>The leftmost column (0, also for a result with no columns at all).</summary>
    public static int FirstColumn(ResultSetViewModel result) => 0;

    /// <summary>The rightmost column; 0 for a result with no columns, so callers can index with it.</summary>
    public static int LastColumn(ResultSetViewModel result) => Math.Max(0, result.Columns.Count - 1);

    /// <summary>The next column from <paramref name="from"/> in direction ±1, or <paramref name="from"/>
    /// itself at an edge (the cursor stops rather than wrapping).</summary>
    public static int StepColumn(ResultSetViewModel result, int from, int dir)
    {
        var to = from + dir;
        return to >= 0 && to < result.Columns.Count ? to : from;
    }

    /// <summary>Where a motion lands from (<paramref name="row"/>, <paramref name="col"/>).
    /// <paramref name="toEdge"/> is the Ctrl modifier — it jumps to the row/column extreme instead of
    /// stepping. Rows and columns both clamp at the edges and stay in range.</summary>
    public static (int Row, int Col) Move(
        ResultSetViewModel result, int row, int col, GridMotion motion, bool toEdge, int pageSize)
    {
        var last = Math.Max(0, result.Rows.Count - 1);
        var page = Math.Max(1, pageSize);
        int nr = Math.Clamp(row, 0, last), nc = col;
        switch (motion)
        {
            case GridMotion.Left: nc = toEdge ? FirstColumn(result) : StepColumn(result, col, -1); break;
            case GridMotion.Right: nc = toEdge ? LastColumn(result) : StepColumn(result, col, +1); break;
            case GridMotion.Up: nr = toEdge ? 0 : Math.Max(0, nr - 1); break;
            case GridMotion.Down: nr = toEdge ? last : Math.Min(last, nr + 1); break;
            case GridMotion.Home: nc = FirstColumn(result); if (toEdge) nr = 0; break;
            case GridMotion.End: nc = LastColumn(result); if (toEdge) nr = last; break;
            case GridMotion.PageUp: nr = Math.Max(0, nr - page); break;
            case GridMotion.PageDown: nr = Math.Min(last, nr + page); break;
        }
        return (nr, nc);
    }

    /// <summary>Every cell in the rectangle spanning <paramref name="a"/>..<paramref name="b"/>
    /// inclusive, in any corner order. Empty when either anchor's row is no longer in the result (a page
    /// reload or a discarded new row can strand one).</summary>
    public static IReadOnlyList<(object?[] Row, int Col)> Rectangle(
        ResultSetViewModel result, (object?[] Row, int Col) a, (object?[] Row, int Col) b)
    {
        var rows = result.Rows;
        int r0 = rows.IndexOf(a.Row), r1 = rows.IndexOf(b.Row);
        if (r0 < 0 || r1 < 0) return Array.Empty<(object?[], int)>();
        if (r0 > r1) (r0, r1) = (r1, r0);
        int c0 = Math.Min(a.Col, b.Col), c1 = Math.Max(a.Col, b.Col);

        var cells = new List<(object?[] Row, int Col)>();
        for (var r = r0; r <= r1; r++)
        {
            var rr = rows[r];
            for (var c = c0; c <= c1; c++)
                if (c < rr.Length && c < result.Columns.Count) cells.Add((rr, c));
        }
        return cells;
    }

    /// <summary>Every cell of the result (Ctrl+A).</summary>
    public static IReadOnlyList<(object?[] Row, int Col)> AllCells(ResultSetViewModel result)
    {
        var cells = new List<(object?[] Row, int Col)>();
        foreach (var row in result.Rows)
            for (var c = 0; c < result.Columns.Count; c++)
                if (c < row.Length) cells.Add((row, c));
        return cells;
    }

    /// <summary>The selection as tab-separated rows, condensed to the selected rows × columns. A
    /// non-rectangular selection keeps its shape by emitting a blank for each gap, so pasted columns still
    /// line up.</summary>
    public static string Tsv(ResultSetViewModel result, IReadOnlyCollection<(object?[] Row, int Col)> cells)
    {
        if (cells.Count == 0) return "";
        var rows = result.Rows;
        var rowIdx = cells.Select(s => rows.IndexOf(s.Row)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        var colIdx = cells.Select(s => s.Col).Distinct().OrderBy(i => i).ToList();
        if (rowIdx.Count == 0) return "";

        var selected = cells as ISet<(object?[] Row, int Col)> ?? cells.ToHashSet();
        return string.Join("\n", rowIdx.Select(ri =>
        {
            var row = rows[ri];
            return string.Join("\t", colIdx.Select(c =>
                selected.Contains((row, c)) && c < row.Length ? CellText(row, c) : ""));
        }));
    }

    /// <summary>The values from the selection that the quick-stats bar may aggregate: measure columns only,
    /// since summing or averaging primary/foreign-key identifiers is meaningless.</summary>
    public static IEnumerable<object?> MeasureValues(
        ResultSetViewModel result, IEnumerable<(object?[] Row, int Col)> cells)
    {
        foreach (var (row, col) in cells)
        {
            if (col >= row.Length || col >= result.Columns.Count) continue;
            var isPk = result.PrimaryKeyColumns.Contains(col);
            var isFk = result.ForeignKeyColumns.Contains(col);
            if (!CellStats.IsMeasureColumn(result.Columns[col].ClrType, isPk, isFk)) continue;
            yield return row[col];
        }
    }

    /// <summary>A cell's display text (the same formatting the grid, inspector and clipboard all use).</summary>
    public static string CellText(object?[]? row, int index)
        => row is not null && index < row.Length ? CellFormat.Display(row[index]) : "";
}
