using System;

namespace Bearing.App.Results;

/// <summary>
/// How tall a result grid is inside an inline zone (spec §6). The editor measures inline objects with infinite
/// height, and a DataGrid measured that way realizes every row, so a zone's grid is always given a definite
/// height: <b>fit to its rows, up to a cap</b>. A three-row result takes three rows of space; a long one stops at
/// the cap and scrolls inside.
/// </summary>
public static class InlineGridSizing
{
    /// <summary>Rows shown before a zone's grid scrolls, until the user drags the grip.</summary>
    public const int DefaultVisibleRows = 10;

    /// <summary>The grip's range, in rows.</summary>
    public const int MinVisibleRows = 2;
    public const int MaxVisibleRows = 60;

    /// <summary>The grid's column-header row (matches the pane's stacked-layout floor).</summary>
    public const double HeaderHeight = 30;

    /// <summary>Room kept for the horizontal scrollbar, so a wide result's last row is not under it.</summary>
    public const double ScrollbarReserve = 12;

    /// <summary>The cap for <paramref name="rows"/> visible rows, in pixels of rows (no header).</summary>
    public static double CapFor(int rows, double rowHeight) => rows * rowHeight;

    /// <summary>The default cap: <see cref="DefaultVisibleRows"/> rows.</summary>
    public static double DefaultCap(double rowHeight) => CapFor(DefaultVisibleRows, rowHeight);

    /// <summary>A dragged cap clamped to the grip's range.</summary>
    public static double ClampCap(double cap, double rowHeight)
        => Math.Clamp(cap, CapFor(MinVisibleRows, rowHeight), CapFor(MaxVisibleRows, rowHeight));

    /// <summary>
    /// The grid's height for <paramref name="rowCount"/> loaded rows under a cap of <paramref name="cap"/> pixels
    /// of rows: the header, then as many rows as there are (at least one, so an empty result still shows its
    /// columns over a blank row), never more than the cap.
    /// </summary>
    public static double GridHeight(int rowCount, double rowHeight, double cap)
    {
        var rows = Math.Max(1, rowCount) * rowHeight;
        return HeaderHeight + Math.Min(rows, Math.Max(cap, rowHeight)) + ScrollbarReserve;
    }
}
