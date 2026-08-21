using System;
using System.Collections.Generic;
using Bearing.App.Formatting;

namespace Bearing.App.Results;

/// <summary>
/// Works out a result column's <i>initial</i> width from what the column actually shows, so the grid opens
/// compact instead of letting the DataGrid's <c>Auto</c> sizing widen every column to its longest realized
/// value (#30). Two numbers per column — how many characters the header needs, how many the widest sampled
/// value needs — clamped between a floor (a column you can still grab and resize) and a ceiling (a long
/// value shows partially and ellipsizes rather than pushing its neighbours off screen).
/// <para>
/// Pure arithmetic over the loaded rows: no grid, no font, no layout. The caller supplies the measured
/// advance of one monospace character and the fixed chrome each side of the text costs (header padding and
/// badges, cell margin and the ⤢/↗ affordance), which is the only part that needs a live typeface. That
/// split is what makes the sizing testable at all — Wayland blocks driving the grid (§4.3).
/// </para>
/// </summary>
public static class ColumnWidths
{
    /// <summary>Rows scanned to size a column. The grid loads 100 at a time, so in practice this is "the
    /// first page or two" — the same bounded-sample approach the xlsx export uses for its column widths.
    /// Later pages don't re-size the column: the user's own resize (or a header double-click to auto-fit)
    /// must not be undone by a scroll.</summary>
    public const int SampleRows = 200;

    /// <summary>Character count past which a value can't affect the result — <see cref="Max"/> px is
    /// reached long before this even with a wide typeface, so scanning further is wasted work on a column
    /// full of documents.</summary>
    private const int ScanCap = 200;

    /// <summary>Narrowest a column starts at. Below this a two-character header (<c>id</c>) would be too
    /// small to aim the resize gripper at.</summary>
    public const double Min = 56;

    /// <summary>Widest a column starts at. A longer value is clipped with an ellipsis (and, past 60
    /// characters, offers the ⤢ inspector); the header can be double-clicked to auto-fit.</summary>
    public const double Max = 280;

    /// <summary>Length of the longest display text this column shows over the sampled rows, counting only
    /// the first line — the cell renders one trimmed line, so a 40-line document is as wide as its first
    /// line, not its total length.</summary>
    public static int ValueChars(IReadOnlyList<object?[]> rows, int index, int sample = SampleRows)
    {
        var widest = 0;
        var count = Math.Min(sample, rows.Count);
        for (var r = 0; r < count; r++)
        {
            var row = rows[r];
            var text = CellFormat.Display(index < row.Length ? row[index] : null);
            var newline = text.IndexOf('\n');
            widest = Math.Max(widest, newline < 0 ? text.Length : newline);
            if (widest >= ScanCap) return ScanCap;
        }
        return widest;
    }

    /// <summary>The column's starting width: whichever of the header and the widest value needs more room,
    /// clamped to <see cref="Min"/>..<see cref="Max"/>. <paramref name="headerExtra"/> and
    /// <paramref name="cellExtra"/> are the non-text pixels on that side (padding, type badges, the inspect
    /// or foreign-key glyph).</summary>
    public static double Initial(
        int headerChars, double headerExtra, int valueChars, double cellExtra, double charWidth)
    {
        var header = headerChars * charWidth + headerExtra;
        var value = valueChars * charWidth + cellExtra;
        return Math.Clamp(Math.Max(header, value), Min, Max);
    }
}
