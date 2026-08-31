using System;
using System.Collections.Generic;
using Bearing.App.Formatting;

namespace Bearing.App.Results;

/// <summary>
/// Works out a result column's <i>initial</i> width from what the column actually shows, so the grid opens
/// compact instead of letting the DataGrid's <c>Auto</c> sizing widen every column to its longest realized
/// value (#30). Two figures per column — what the header's text needs, what the widest sampled value needs —
/// clamped between a floor (a column you can still grab and resize) and a ceiling (a long value shows
/// partially and ellipsizes rather than pushing its neighbours off screen).
/// <para>
/// This decides the arithmetic; the caller measures the text. It used to take a character count and one
/// character's average advance and multiply, which is a reconstruction rather than a measurement: shaped text
/// is not <c>N ×</c> the mean advance, and the error ran ~1px per character in the direction that clips
/// (#73 — a six-character id needed 52px in a column sized to 45). Handing in a measured width removes that
/// whole class, and costs one <c>FormattedText</c> per column instead of per cell.
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
    /// reached long before this even with a narrow typeface, so neither scanning nor measuring further is
    /// worth doing on a column full of documents.</summary>
    private const int ScanCap = 200;

    /// <summary>Narrowest a column starts at. Below this a two-character header (<c>id</c>) would be too
    /// small to aim the resize gripper at.</summary>
    public const double Min = 56;

    /// <summary>Widest a column starts at. A longer value is clipped with an ellipsis (and, past 60
    /// characters, offers the ⤢ inspector); the header can be double-clicked to auto-fit.</summary>
    public const double Max = 280;

    /// <summary>How many candidate values the caller is handed to measure. One is only sound on a truly
    /// monospace face: <c>MonoFont</c> is a fallback stack, and if it lands on a proportional family the
    /// longest string is not the widest — <c>iiiiiiiiii</c> beats <c>WWWWWWWWW</c> on characters and loses
    /// badly on pixels. A handful covers that without measuring every sampled cell.</summary>
    private const int Candidates = 4;

    /// <summary>
    /// The widest display texts this column shows over the sampled rows, longest first, for the caller to
    /// measure and take the maximum of. Only the first line of each counts — the cell renders one trimmed
    /// line, so a 40-line document is as wide as its first line, not its total length. Each is capped at
    /// <see cref="ScanCap"/> characters, which is far past <see cref="Max"/>.
    /// <para>
    /// Never empty: an empty result yields a single empty string, so the caller can measure unconditionally.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> WidestValues(
        IReadOnlyList<object?[]> rows, int index, int sample = SampleRows)
    {
        var seen = new List<string>();
        var count = Math.Min(sample, rows.Count);
        for (var r = 0; r < count; r++)
        {
            var row = rows[r];
            var text = CellFormat.Display(index < row.Length ? row[index] : null);
            var newline = text.IndexOf('\n');
            if (newline >= 0) text = text[..newline];
            if (text.Length > ScanCap) text = text[..ScanCap];

            // Keep the longest few, longest first. Anything shorter than the shortest kept candidate cannot
            // win on a monospace face and is a poor bet on any other, so it is dropped without measuring.
            if (seen.Count == Candidates && text.Length <= seen[^1].Length) continue;
            var at = seen.FindIndex(v => v.Length < text.Length);
            seen.Insert(at < 0 ? seen.Count : at, text);
            if (seen.Count > Candidates) seen.RemoveAt(seen.Count - 1);
        }
        return seen.Count > 0 ? seen : [""];
    }

    /// <summary>The single widest sampled value by character count — the candidate list's first entry.
    /// Sound where the face really is monospace; prefer <see cref="WidestValues"/> where the answer is going
    /// to be measured.</summary>
    public static string WidestValue(IReadOnlyList<object?[]> rows, int index, int sample = SampleRows)
        => WidestValues(rows, index, sample)[0];

    /// <summary>
    /// Whether any sampled value will render the ⤢ inspect affordance, so the column can reserve room for it
    /// rather than letting it eat the value. The glyph appears on a value past
    /// <paramref name="maxInlineChars"/> or containing a newline, and the newline case is the one that hurt:
    /// <see cref="WidestValue"/> stops at the first line, so a document whose first line is short sizes the
    /// column to <see cref="Min"/> and the unreserved glyph then leaves the text a few pixels.
    /// </summary>
    public static bool AnyValueInspectable(
        IReadOnlyList<object?[]> rows, int index, int maxInlineChars, int sample = SampleRows)
    {
        var count = Math.Min(sample, rows.Count);
        for (var r = 0; r < count; r++)
        {
            var row = rows[r];
            var text = CellFormat.Display(index < row.Length ? row[index] : null);
            if (text.Length > maxInlineChars || text.Contains('\n')) return true;
        }
        return false;
    }

    /// <summary>The column's starting width: whichever of the header and the widest value needs more room,
    /// clamped to <see cref="Min"/>..<see cref="Max"/>. The two <c>extra</c> figures are the non-text pixels
    /// on that side — padding, the column divider, type badges, the inspect or foreign-key glyph — and have
    /// to account for the same things on both sides, which is the other half of #73: the header reserved the
    /// 1px divider and the cell never did.</summary>
    public static double Initial(
        double headerTextWidth, double headerExtra, double valueTextWidth, double cellExtra)
        => Math.Clamp(
            Math.Max(headerTextWidth + headerExtra, valueTextWidth + cellExtra), Min, Max);
}
