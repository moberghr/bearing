using System;
using System.Collections.Generic;
using Bearing.Sql;

namespace Bearing.App.Results;

/// <summary>
/// Where an inline zone goes when a command rewrites the buffer in one edit (Ctrl+/ toggling comments, Format).
/// Such an edit replaces a span wider than what really changed, so an anchor alone would read every zone in it
/// as deleted. Pure.
/// <para>
/// Two answers, tried in order, neither guessed: a zone sitting exactly on a statement's end follows that
/// statement by index when both texts have the same number of statements; otherwise a zone outside the span
/// that really differs (common prefix / suffix) moves with the text around it. Anything else is null — the
/// text it hung under is gone, and the caller treats it as deleted.
/// </para>
/// </summary>
internal static class InlineZoneRemap
{
    /// <summary>The offsets in <paramref name="after"/> for zones at <paramref name="offsets"/> in
    /// <paramref name="before"/>; null where the zone's text did not survive.</summary>
    public static IReadOnlyList<int?> Map(ISqlDialect dialect, string before, string after, IReadOnlyList<int> offsets)
    {
        var result = new int?[offsets.Count];
        if (offsets.Count == 0)
        {
            return result;
        }
        var endsBefore = InlineResultPlacement.Scope(dialect, before, 0, isWholeBuffer: true).StatementEnds;
        var endsAfter = InlineResultPlacement.Scope(dialect, after, 0, isWholeBuffer: true).StatementEnds;
        var sameShape = endsBefore.Count == endsAfter.Count;

        var max = Math.Min(before.Length, after.Length);
        var prefix = 0;
        while (prefix < max && before[prefix] == after[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }
        var delta = after.Length - before.Length;

        for (var i = 0; i < offsets.Count; i++)
        {
            var offset = offsets[i];
            var statement = sameShape ? IndexOf(endsBefore, offset) : -1;
            if (statement >= 0)
            {
                result[i] = endsAfter[statement];
            }
            else if (offset <= prefix)
            {
                result[i] = offset;
            }
            else if (offset >= before.Length - suffix)
            {
                result[i] = offset + delta;
            }
        }
        return result;
    }

    private static int IndexOf(IReadOnlyList<int> ends, int offset)
    {
        for (var i = 0; i < ends.Count; i++)
        {
            if (ends[i] == offset)
            {
                return i;
            }
        }
        return -1;
    }
}
