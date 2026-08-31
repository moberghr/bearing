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
    /// badly on pixels.</summary>
    private const int Candidates = 4;

    /// <summary>What one sampling pass over a column found: the values worth measuring, and whether any of
    /// them will carry the inspect affordance the column has to reserve room for.</summary>
    public readonly record struct ColumnSample(IReadOnlyList<string> Candidates, bool AnyInspectable);

    /// <summary>
    /// Walk the sampled rows once and answer both questions the caller has about a column.
    /// <para>
    /// One pass because it used to be two, each formatting every sampled cell — 200 rows x 2 x every column
    /// at grid-build time, for answers that come off the same strings.
    /// </para>
    /// <para>
    /// Candidates are ranked by an estimated width rather than by length, for the proportional-fallback case
    /// above: picking by length alone drops a genuinely wider but shorter value as soon as enough longer ones
    /// exist. Only the first line of each counts — the cell renders one trimmed line, so a 40-line document
    /// is as wide as its first line — and each is capped at <see cref="ScanCap"/> characters, far past
    /// <see cref="Max"/>. Never empty: an empty result yields one empty string, so the caller can measure
    /// unconditionally.
    /// </para>
    /// </summary>
    /// <param name="maxInlineChars">Length past which a value grows the inspect affordance. Any newline does
    /// too, which is the case that hurt: a document with a short first line sizes its column to
    /// <see cref="Min"/>, and an unreserved glyph then leaves the text a few pixels.</param>
    public static ColumnSample Sample(
        IReadOnlyList<object?[]> rows, int index, int maxInlineChars, int sample = SampleRows)
    {
        var best = new List<string>();
        var anyInspectable = false;
        var count = Math.Min(sample, rows.Count);
        for (var r = 0; r < count; r++)
        {
            var row = rows[r];
            var text = CellFormat.Display(index < row.Length ? row[index] : null);
            anyInspectable |= text.Length > maxInlineChars || text.Contains('\n');

            var newline = text.IndexOf('\n');
            if (newline >= 0) text = text[..newline];
            if (text.Length > ScanCap) text = text[..ScanCap];

            Keep(best, text);
            // Nothing further can change either answer: the widest candidate is already at the cap, and a
            // value that long has already set the affordance flag.
            if (anyInspectable && best[0].Length >= ScanCap) break;
        }
        return new ColumnSample(best.Count > 0 ? best : [""], anyInspectable);
    }

    /// <summary>Insert <paramref name="text"/> into the widest-first shortlist, keeping it at
    /// <see cref="Candidates"/>.</summary>
    private static void Keep(List<string> best, string text)
    {
        var width = EstimatedWidth(text);
        if (best.Count == Candidates && width <= EstimatedWidth(best[^1])) return;

        var at = best.FindIndex(v => EstimatedWidth(v) < width);
        best.Insert(at < 0 ? best.Count : at, text);
        if (best.Count > Candidates) best.RemoveAt(best.Count - 1);
    }

    /// <summary>A cheap stand-in for how wide a string will draw, used only to rank candidates — the caller
    /// measures the winners for real. Capitals and the handful of famously wide lowercase glyphs count for
    /// more than a narrow <c>i</c>, which is the whole difference between this and a character count.</summary>
    private static double EstimatedWidth(string text)
    {
        var width = 0d;
        foreach (var c in text) width += char.IsUpper(c) || c is 'm' or 'w' or '@' or '%' ? 1.3 : 1;
        return width;
    }

    /// <summary>The single widest sampled value, by the same estimate. Sound where the face really is
    /// monospace; prefer <see cref="Sample"/> where the answer is going to be measured.</summary>
    public static string WidestValue(IReadOnlyList<object?[]> rows, int index, int sample = SampleRows)
        => Sample(rows, index, int.MaxValue, sample).Candidates[0];

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
