using System;
using System.Collections.Generic;
using Bearing.App.ViewModels;

namespace Bearing.App.Results;

/// <summary>
/// The shape rules for pasting into a results grid: clipboard text → a rectangle of cell texts → the exact
/// list of cells to write. Pure over <see cref="ResultSetViewModel"/> (no clipboard, no controls), because
/// the paste *shape* is precisely the part that has to be right and precisely the part Wayland stops us
/// checking by hand (§4.3, §2.5). The writing itself is one <c>SetCell</c> per plan entry, so paste inherits
/// the in-cell editor's whole value path — the <c>(null)</c> token, empty-means-NULL, and save-time coercion.
/// <para>
/// Two behaviours, matching what a spreadsheet does:
/// </para>
/// <list type="bullet">
/// <item>A <b>single clipboard value</b> fills every selected cell — copy one value, sweep a block, paste.</item>
/// <item>A <b>block</b> anchors at the active cell and fills right/down from there, <i>past</i> the selection
/// if it is bigger than it. It never grows the result: cells beyond the last loaded row or the last column
/// are dropped rather than inserting rows or inventing columns.</item>
/// </list>
/// </summary>
public static class GridPaste
{
    /// <summary>Clipboard text as a rectangle of cell texts: newline splits rows, tab splits columns. A
    /// trailing newline is ignored (every "copy" that ends with one would otherwise paste a blank row).
    /// <para>
    /// No quote handling, deliberately: <see cref="GridSelectionOps.Tsv"/> doesn't quote either, so this is
    /// the exact inverse of what Copy produces. A value containing a tab or a newline can't survive a TSV
    /// round trip in any case — Copy as ▸ CSV/JSON is the lossless path.
    /// </para></summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<IReadOnlyList<string>>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var end = lines.Length;
        while (end > 1 && lines[end - 1].Length == 0) end--; // trailing blank lines only
        var block = new List<IReadOnlyList<string>>(end);
        for (var i = 0; i < end; i++) block.Add(lines[i].Split('\t'));
        return block;
    }

    /// <summary>Which cells a paste writes, and what into each — row-major, no duplicates. Empty when there
    /// is nothing to paste or nowhere to put it.</summary>
    /// <param name="active">The anchor a block pastes from (the cell the cursor is on).</param>
    /// <param name="selection">The current selection, which a single clipboard value fills.</param>
    public static IReadOnlyList<(object?[] Row, int Col, string Text)> Plan(
        ResultSetViewModel result,
        IReadOnlyList<IReadOnlyList<string>> block,
        (object?[] Row, int Col) active,
        IReadOnlyCollection<(object?[] Row, int Col)> selection)
    {
        var writes = new List<(object?[] Row, int Col, string Text)>();
        if (block.Count == 0) return writes;

        // One value fills the selection (or just the active cell, if a command ran with nothing selected).
        if (block.Count == 1 && block[0].Count == 1)
        {
            var value = block[0][0];
            var targets = selection.Count > 0
                ? Ordered(result, selection)
                : new List<(object?[] Row, int Col)> { active };
            foreach (var (row, col) in targets)
                if (InRange(result, row, col)) writes.Add((row, col, value));
            return writes;
        }

        var anchor = result.Rows.IndexOf(active.Row);
        if (anchor < 0) return writes; // the cursor's row is gone (a discarded new row) — nothing to anchor on

        for (var r = 0; r < block.Count; r++)
        {
            var target = anchor + r;
            if (target >= result.Rows.Count) break; // clipped: paste never appends rows
            var row = result.Rows[target];
            for (var c = 0; c < block[r].Count; c++)
            {
                var col = active.Col + c;
                if (!InRange(result, row, col)) break; // clipped at the right edge
                writes.Add((row, col, block[r][c]));
            }
        }
        return writes;
    }

    /// <summary>How many cells of <paramref name="block"/> a paste would drop for want of rows or columns —
    /// what the status line reports, so a silently truncated paste can't look like a complete one.</summary>
    public static int Clipped(
        ResultSetViewModel result,
        IReadOnlyList<IReadOnlyList<string>> block,
        (object?[] Row, int Col) active,
        IReadOnlyCollection<(object?[] Row, int Col)> selection)
    {
        var total = 0;
        foreach (var row in block) total += row.Count;
        if (block.Count == 1 && block[0].Count == 1) return 0; // a fill writes one value, it can't overflow
        return Math.Max(0, total - Plan(result, block, active, selection).Count);
    }

    private static bool InRange(ResultSetViewModel result, object?[] row, int col)
        => col >= 0 && col < result.Columns.Count && col < row.Length;

    /// <summary>The selection in row-then-column order, so a fill writes deterministically (the model keeps
    /// it in a hash set).</summary>
    private static List<(object?[] Row, int Col)> Ordered(
        ResultSetViewModel result, IReadOnlyCollection<(object?[] Row, int Col)> selection)
    {
        var ordered = new List<(object?[] Row, int Col)>(selection);
        ordered.Sort((a, b) =>
        {
            var byRow = result.Rows.IndexOf(a.Row).CompareTo(result.Rows.IndexOf(b.Row));
            return byRow != 0 ? byRow : a.Col.CompareTo(b.Col);
        });
        return ordered;
    }
}
