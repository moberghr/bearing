using System;

namespace Bearing.App.ViewModels;

/// <summary>
/// The pure half of the Scripts filter: what a filter matches, and the line to show as the reason it did.
/// Extracted so it can be tested without a tree or a project (§2.5 / §4.3), following
/// <see cref="SchemaTreeSearch"/>.
/// <para>
/// Substring and case-insensitive, deliberately — not the subsequence matching
/// <see cref="SchemaTreeSearch.FuzzyMatch"/> and <c>PaletteFilter</c> use. Fuzzy is right for short names
/// and useless against a few KB of SQL, where the query's letters appear in order in essentially every
/// file, so every file would match.
/// </para>
/// </summary>
internal static class ScriptSearch
{
    /// <summary>Below this many characters the filter stays name-only. One or two letters occur inside
    /// nearly every script, so a content pass would return the whole tree — for the price of reading every
    /// file — and say nothing.</summary>
    public const int MinContentFilterLength = 3;

    /// <summary>Files larger than this are skipped by the content pass rather than read: a dump that landed
    /// in the scripts folder must not stall the panel. Their names are still matched.</summary>
    public const long MaxContentBytes = 1L << 20; // 1 MiB

    /// <summary>How much of the matching line is kept for display.</summary>
    public const int MaxMatchLineLength = 90;

    /// <summary>The original name filter: an empty filter matches everything.</summary>
    public static bool MatchesName(string name, string filter)
        => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="filter"/> is worth reading file contents for.</summary>
    public static bool WantsContentSearch(string filter)
        => filter.Length >= MinContentFilterLength;

    /// <summary>
    /// The first line of <paramref name="text"/> containing <paramref name="filter"/> — the "why did this
    /// match?" line shown under the file name — or null when the text doesn't contain it at all, which is
    /// also the answer to "is this a content hit?". Whitespace-collapsed and capped, because it is rendered
    /// on one line; a line that is nothing but the match keeps the filter itself as its text, so a hit is
    /// never reported without a reason.
    /// </summary>
    public static string? MatchingLine(string text, string filter)
    {
        if (filter.Length == 0 || text.Length == 0) return null;
        var hit = text.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
        if (hit < 0) return null;

        var start = hit == 0 ? 0 : text.LastIndexOfAny(NewLine, hit - 1) + 1;
        var end = text.IndexOfAny(NewLine, hit);
        if (end < 0) end = text.Length;

        var line = Flatten(text[start..end]);
        if (line.Length == 0) line = filter;
        return line.Length > MaxMatchLineLength ? line[..MaxMatchLineLength] + "…" : line;
    }

    private static readonly char[] NewLine = { '\n', '\r' };

    /// <summary>Tabs and runs of spaces to single spaces: SQL is indented, and a snippet that keeps its
    /// leading indent reads as if it were blank.</summary>
    private static string Flatten(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        var space = false;
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
