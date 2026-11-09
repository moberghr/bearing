using System;
using System.Collections.Generic;

namespace Bearing.App.Results;

/// <summary>
/// A rewrite of the buffer (Ctrl+/, Format) as the smallest edits that turn the old text into the new one,
/// rather than one replace over all of it. Text that didn't change is never touched, so a text anchor in it — an
/// inline zone's — simply moves with the edit, its undo and its redo; one replace over everything would read every
/// such anchor as deleted. Applied to the editor by <see cref="Editing.TextHunkApply"/>.
/// <para>
/// <b>How.</b> Trim the common prefix and suffix; diff the middle by lines (Myers); diff each changed run of lines
/// by characters (Myers again), so commenting a line inserts <c>-- </c> and nothing else. Each diff has an
/// edit-distance cap; past it the run falls back to one hunk trimmed of its common ends. Pure.
/// </para>
/// </summary>
internal static class TextHunks
{
    /// <summary>Replace <see cref="Length"/> characters at <see cref="Offset"/> (in the old text) with
    /// <see cref="Text"/>.</summary>
    public readonly record struct Hunk(int Offset, int Length, string Text);

    /// <summary>Line-level edit distance past which a rewrite is diffed as one run.</summary>
    public const int MaxLineEdits = 1000;

    /// <summary>Character-level edit distance past which one run of changed lines is one hunk.</summary>
    public const int MaxCharEdits = 1000;

    /// <summary>Longest run of changed lines (in characters, old plus new) diffed by character.</summary>
    public const int MaxCharRun = 20_000;

    /// <summary>The hunks turning <paramref name="before"/> into <paramref name="after"/>, in order, not
    /// overlapping, offsets in <paramref name="before"/>.</summary>
    /// <param name="sync">Offset pairs (before, after) that must be hunk boundaries — never strictly inside a
    /// hunk — such as matching statement ends (<see cref="StatementSync"/>): an anchor sitting on one then
    /// survives the edit, its undo and its redo, however much the text around it changed. Pairs out of order or
    /// out of range are ignored.</param>
    /// <param name="maxWork">Total diff steps across every run; past it the whole rewrite falls back to one
    /// hunk per stretch between sync points (trimmed of its common ends), so a huge rewrite stays bounded and the
    /// sync points stay boundaries.</param>
    public static IReadOnlyList<Hunk> Diff(string before, string after, IReadOnlyList<(int Before, int After)>? sync = null,
        long maxWork = MaxWork)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return [];
        }
        try
        {
            return Segments(before, after, sync, new Work(maxWork));
        }
        catch (OutOfWork)
        {
            return Segments(before, after, sync, work: null);
        }
    }

    /// <summary>More edits than this and a rewrite is applied as one trimmed replace (<see cref="Whole"/>): a few
    /// thousand Replace calls cost more than the anchors they keep.</summary>
    public const int MaxHunks = 500;

    /// <summary>Default total budget for <see cref="Diff"/>: tens of milliseconds of diffing at most.</summary>
    public const long MaxWork = 20_000_000;

    /// <summary>One rewrite's diff budget, and the Myers buffers every run of it reuses — the trace is O(D²),
    /// and a new one per changed run churned memory on a big Format.</summary>
    private sealed class Work(long left)
    {
        public long Left = left;
        public int[] V = [];
        public int[] Trace = [];
    }

    private sealed class OutOfWork : Exception;

    /// <summary>Each stretch between sync points: diffed under <paramref name="work"/>, or (null) one trimmed hunk.</summary>
    private static List<Hunk> Segments(string before, string after, IReadOnlyList<(int Before, int After)>? sync, Work? work)
    {
        var hunks = new List<Hunk>();
        int b = 0, a = 0;
        foreach (var (sb, sa) in sync ?? [])
        {
            if (sb < b || sa < a || sb > before.Length || sa > after.Length)
            {
                continue;
            }
            var first = hunks.Count;
            Segment(hunks, before, b, sb, after, a, sa, work);
            EndOnSyncPoint(hunks, first, before, b, sb);
            b = sb;
            a = sa;
        }
        Segment(hunks, before, b, before.Length, after, a, after.Length, work);
        return hunks;
    }

    /// <summary>
    /// A stretch whose last hunk is a pure insertion right at the sync point it ends on (text added just before a
    /// statement end) would leave an anchor there before the new text — a text anchor stays ahead of an insertion
    /// at its offset — and so off the new statement end. Widened to replace the character before it, it ends on
    /// the anchor as a replace, and a replace carries an anchor at its end to the new end (and back on undo).
    /// </summary>
    private static void EndOnSyncPoint(List<Hunk> hunks, int first, string before, int start, int syncPoint)
    {
        if (hunks.Count == first || hunks[^1] is not { Length: 0 } last || last.Offset != syncPoint || syncPoint <= start)
        {
            return;
        }
        if (hunks.Count - 1 > first && hunks[^2].Offset + hunks[^2].Length > syncPoint - 1)
        {
            return;   // the character before is already another hunk's (never adjacent after a diff, but be sure)
        }
        hunks[^1] = new Hunk(syncPoint - 1, 1, before[syncPoint - 1] + last.Text);
    }

    /// <summary>The whole rewrite as one hunk without its common ends — no diff at all (nothing hangs on the text
    /// to keep, so there is no reason to pay for one).</summary>
    public static IReadOnlyList<Hunk> Whole(string before, string after)
    {
        var hunks = new List<Hunk>();
        Segment(hunks, before, 0, before.Length, after, 0, after.Length, work: null);
        return hunks;
    }

    /// <summary>Matching statement ends of <paramref name="before"/> and <paramref name="after"/> — where inline
    /// zones hang — when both hold the same number of statements (a reformat); none otherwise.</summary>
    public static IReadOnlyList<(int Before, int After)> StatementSync(Bearing.Sql.ISqlDialect dialect, string before, string after)
    {
        var endsBefore = InlineResultPlacement.StatementEnds(dialect, before);
        var endsAfter = InlineResultPlacement.StatementEnds(dialect, after);
        if (endsBefore.Count != endsAfter.Count)
        {
            return [];
        }
        var pairs = new List<(int, int)>(endsBefore.Count);
        for (var i = 0; i < endsBefore.Count; i++)
        {
            pairs.Add((endsBefore[i], endsAfter[i]));
        }
        return pairs;
    }

    /// <summary>
    /// The hunks for a rewrite that keeps the line count — Ctrl+/ adding or removing <c>-- </c>: line i becomes
    /// line i, so each changed line pair is one hunk without its common ends. Linear, no diff; a rewrite that
    /// changes the line count is one hunk over the whole text, trimmed of its common ends (still no diff).
    /// </summary>
    public static IReadOnlyList<Hunk> LineByLine(string before, string after)
    {
        var bLines = Lines(before, 0, before.Length);
        var aLines = Lines(after, 0, after.Length);
        if (bLines.Count != aLines.Count)
        {
            return Whole(before, after);   // not line-for-line: one trimmed hunk — never a diff on the UI thread
        }
        var hunks = new List<Hunk>();
        for (var i = 0; i < bLines.Count; i++)
        {
            var (bs, bl) = bLines[i];
            var (aStart, al) = aLines[i];
            if (bl == al && string.CompareOrdinal(before, bs, after, aStart, bl) == 0)
            {
                continue;
            }
            var (prefix, suffix) = CommonEnds(before, bs, bs + bl, after, aStart, aStart + al);
            hunks.Add(new Hunk(bs + prefix, bl - prefix - suffix, after.Substring(aStart + prefix, al - prefix - suffix)));
        }
        return hunks;
    }

    /// <summary>One stretch, before[bStart, bEnd) → after[aStart, aEnd): common ends trimmed, then by lines,
    /// then each changed run of lines by characters.</summary>
    private static void Segment(List<Hunk> hunks, string before, int bStart, int bEnd, string after, int aStart, int aEnd,
        Work? work)
    {
        var (prefix, suffix) = CommonEnds(before, bStart, bEnd, after, aStart, aEnd);
        bStart += prefix;
        aStart += prefix;
        bEnd -= suffix;
        aEnd -= suffix;
        if (bStart == bEnd && aStart == aEnd)
        {
            return;
        }
        if (work is null)
        {
            hunks.Add(new Hunk(bStart, bEnd - bStart, after[aStart..aEnd]));
            return;
        }
        var bLines = Lines(before, bStart, bEnd);
        var aLines = Lines(after, aStart, aEnd);
        var matches = Myers(bLines.Count, aLines.Count, MaxLineEdits, work,
            (i, j) => bLines[i].Length == aLines[j].Length
                && string.CompareOrdinal(before, bLines[i].Start, after, aLines[j].Start, bLines[i].Length) == 0);
        if (matches is null)
        {
            AddRun(hunks, before, bStart, bEnd, after, aStart, aEnd, work);
            return;
        }
        int bi = 0, ai = 0;
        foreach (var (mi, mj) in matches)
        {
            AddRun(hunks, before, Start(bLines, bi, bStart), Start(bLines, mi, bEnd),
                after, Start(aLines, ai, aStart), Start(aLines, mj, aEnd), work);
            bi = mi + 1;
            ai = mj + 1;
        }
        AddRun(hunks, before, Start(bLines, bi, bEnd), bEnd, after, Start(aLines, ai, aEnd), aEnd, work);
    }

    /// <summary>One changed run (old [bStart, bEnd) → new [aStart, aEnd)): by character when small enough,
    /// else one hunk without its common ends.</summary>
    private static void AddRun(List<Hunk> hunks, string before, int bStart, int bEnd, string after, int aStart, int aEnd,
        Work work)
    {
        if (bStart == bEnd && aStart == aEnd)
        {
            return;
        }
        var (prefix, suffix) = CommonEnds(before, bStart, bEnd, after, aStart, aEnd);
        bStart += prefix;
        aStart += prefix;
        bEnd -= suffix;
        aEnd -= suffix;
        if (bStart == bEnd && aStart == aEnd)
        {
            return;
        }
        var matches = (bEnd - bStart) + (aEnd - aStart) <= MaxCharRun
            ? Myers(bEnd - bStart, aEnd - aStart, MaxCharEdits, work, (i, j) => before[bStart + i] == after[aStart + j])
            : null;
        if (matches is null)
        {
            hunks.Add(new Hunk(bStart, bEnd - bStart, after[aStart..aEnd]));
            return;
        }
        int bi = 0, ai = 0;
        foreach (var (mi, mj) in matches)
        {
            Gap(hunks, before, bStart + bi, bStart + mi, after, aStart + ai, aStart + mj);
            bi = mi + 1;
            ai = mj + 1;
        }
        Gap(hunks, before, bStart + bi, bEnd, after, aStart + ai, aEnd);
    }

    private static void Gap(List<Hunk> hunks, string before, int bStart, int bEnd, string after, int aStart, int aEnd)
    {
        if (bStart < bEnd || aStart < aEnd)
        {
            hunks.Add(new Hunk(bStart, bEnd - bStart, after[aStart..aEnd]));
        }
    }

    /// <summary>The common prefix and suffix (not overlapping) of before[bStart, bEnd) and after[aStart, aEnd).</summary>
    internal static (int Prefix, int Suffix) CommonEnds(string b, int bStart, int bEnd, string a, int aStart, int aEnd)
    {
        var max = Math.Min(bEnd - bStart, aEnd - aStart);
        var prefix = 0;
        while (prefix < max && b[bStart + prefix] == a[aStart + prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < max - prefix && b[bEnd - 1 - suffix] == a[aEnd - 1 - suffix])
        {
            suffix++;
        }
        return (prefix, suffix);
    }

    /// <summary>The lines of text[start, end), each with its line break.</summary>
    private static List<(int Start, int Length)> Lines(string text, int start, int end)
    {
        var lines = new List<(int, int)>();
        var at = start;
        while (at < end)
        {
            var newline = text.IndexOf('\n', at, end - at);
            var next = newline < 0 ? end : newline + 1;
            lines.Add((at, next - at));
            at = next;
        }
        return lines;
    }

    private static int Start(List<(int Start, int Length)> lines, int index, int end)
        => index < lines.Count ? lines[index].Start : end;

    /// <summary>
    /// The matched pairs (i, j) of a shortest edit script between sequences of length <paramref name="n"/> and
    /// <paramref name="m"/> (Myers' O(ND) algorithm), in order; null when more than <paramref name="maxEdits"/>
    /// edits are needed. Memory is O(D²) for the trace.
    /// </summary>
    internal static List<(int I, int J)>? Myers(int n, int m, int maxEdits, Func<int, int, bool> equal)
        => Myers(n, m, maxEdits, new Work(long.MaxValue), equal);

    /// <summary>The same, charging each step to <paramref name="work"/>; throws <see cref="OutOfWork"/> when it
    /// runs out.</summary>
    private static List<(int I, int J)>? Myers(int n, int m, int maxEdits, Work work, Func<int, int, bool> equal)
    {
        var limit = Math.Min(n + m, maxEdits);   // k never leaves [-limit, limit]; V reads one past either end
        var offset = limit + 1;
        var v = Rent(ref work.V, 2 * limit + 3);
        v[offset + 1] = 0;                       // the one entry round 0 reads before anything writes it
        // Round d's live window (k = -d - 1 .. d + 1, 2d + 3 entries) is stored at Base(d) = d² + 2d, one after
        // another in a buffer shared by every run of the rewrite, grown round by round — a diff that ends at a
        // small d never pays for the cap.
        for (var d = 0; d <= limit; d++)
        {
            var trace = Rent(ref work.Trace, Base(d + 1));
            Array.Copy(v, offset - d - 1, trace, Base(d), 2 * d + 3);
            for (var k = -d; k <= d; k += 2)
            {
                Spend(work);
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && equal(x, y))
                {
                    Spend(work);
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(work.Trace, n, m, d);
                }
            }
        }
        return null;
    }

    private static int Base(int d) => d * d + 2 * d;

    /// <summary><paramref name="buffer"/>, grown to at least <paramref name="size"/> with its contents kept (never
    /// shrunk).</summary>
    private static int[] Rent(ref int[] buffer, int size)
    {
        if (buffer.Length < size)
        {
            Array.Resize(ref buffer, Math.Max(size, buffer.Length * 2));   // keeps the rounds already traced
        }
        return buffer;
    }

    private static void Spend(Work work)
    {
        if (--work.Left < 0)
        {
            throw new OutOfWork();
        }
    }

    private static List<(int, int)> Backtrack(int[] trace, int n, int m, int depth)
    {
        var pairs = new List<(int, int)>();
        int x = n, y = m;
        for (var d = depth; d > 0; d--)
        {
            // Round d's window: v as round d found it (after round d - 1), indices -d - 1 .. d + 1.
            var window = Base(d) + d + 1;
            int V(int k) => trace[window + k];
            var k = x - y;
            var previousK = k == -d || (k != d && V(k - 1) < V(k + 1)) ? k + 1 : k - 1;
            var previousX = V(previousK);
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY)
            {
                pairs.Add((x - 1, y - 1));
                x--;
                y--;
            }
            x = previousX;
            y = previousY;
        }
        while (x > 0 && y > 0)
        {
            pairs.Add((x - 1, y - 1));
            x--;
            y--;
        }
        pairs.Reverse();
        return pairs;
    }
}
