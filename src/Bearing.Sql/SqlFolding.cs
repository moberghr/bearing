using System.Collections.Generic;

namespace Bearing.Sql;

/// <summary>A collapsible region: the buffer span hidden when its statement is folded.</summary>
public sealed record FoldRegion(int Start, int End);

/// <summary>
/// Derives one foldable region per multi-line top-level statement, reusing
/// <see cref="StatementSplitter"/> for the boundaries. A region begins at the end of the
/// statement's first line — so folding leaves that first line (often a leading comment)
/// visible — and runs to the statement's last non-whitespace character.
/// </summary>
public static class SqlFolding
{
    public static IReadOnlyList<FoldRegion> ComputeFoldRegions(string sql)
    {
        var regions = new List<FoldRegion>();
        if (string.IsNullOrEmpty(sql)) return regions;

        foreach (var span in StatementSplitter.Split(sql))
        {
            if (string.IsNullOrWhiteSpace(span.Text)) continue;

            var end = span.TrimmedEnd;
            var firstLineEnd = sql.IndexOf('\n', span.TrimmedStart);
            // Single-line statement (no newline, or the newline sits past its content): nothing to fold.
            if (firstLineEnd < 0 || firstLineEnd >= end) continue;

            regions.Add(new FoldRegion(firstLineEnd, end));
        }
        return regions;
    }
}
