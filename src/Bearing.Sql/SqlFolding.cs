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
    /// <summary>Fold regions for a Postgres buffer.</summary>
    public static IReadOnlyList<FoldRegion> ComputeFoldRegions(string sql)
        => ComputeFoldRegions(PostgresDialect.Instance, sql);

    /// <summary>
    /// Fold regions for a buffer belonging to <paramref name="dialect"/>. Folding needs no grammar — a
    /// region is one statement minus its first line — so the dialect is here only to draw the boundaries,
    /// and a T-SQL buffer folds per <c>GO</c>-separated batch rather than per semicolon.
    /// </summary>
    public static IReadOnlyList<FoldRegion> ComputeFoldRegions(ISqlDialect dialect, string sql)
    {
        var regions = new List<FoldRegion>();
        if (string.IsNullOrEmpty(sql)) return regions;

        foreach (var span in StatementSplitter.Split(dialect, sql))
        {
            if (string.IsNullOrWhiteSpace(span.Text)) continue;

            var end = span.TrimmedEnd;
            var firstLineEnd = sql.IndexOf('\n', span.TrimmedStart);
            // Single-line statement (no newline, or the newline sits past its content): nothing to fold.
            if (firstLineEnd < 0 || firstLineEnd >= end) continue;

            // Back up over a CR so the region starts at the end of the line's *text*, never inside the line
            // delimiter. Not cosmetic: the fold margin looks for a folding whose start lies within a line, and
            // a document line ends at the CR — so under CRLF an offset at the LF is one past the line and the
            // margin creates no marker for it. The section still exists and the fold commands still work,
            // which is exactly why this hid: everything reported six foldings and the gutter was empty. Under
            // LF the two positions coincide, which is why every LF fixture passed.
            if (firstLineEnd > 0 && sql[firstLineEnd - 1] == '\r') firstLineEnd--;
            if (firstLineEnd <= span.TrimmedStart) continue;   // an empty first line folds nothing

            regions.Add(new FoldRegion(firstLineEnd, end));
        }
        return regions;
    }
}
