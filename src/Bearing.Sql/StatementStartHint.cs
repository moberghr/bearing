namespace Bearing.Sql;

/// <summary>
/// Whether the caret looks like the start of a new statement being typed, for completion only (#68).
/// <para>
/// Write a query, press Enter, start typing the next one, and <c>select</c> was not offered: the caret is
/// still inside the previous statement as far as <see cref="StatementSplitter"/> is concerned — a lone
/// newline is deliberately not a boundary — so the grammar is asked what can <i>continue</i>
/// <c>select * from users</c>, and <c>SELECT</c> is not one of those things. Terminating with a
/// <c>;</c> fixed it, which is exactly the workaround the report is about.
/// </para>
/// <para>
/// This is deliberately <b>not</b> a change to <see cref="StatementSplitter"/>. That splitter decides what
/// Run-at-caret executes, what folds, and what the destructive-SQL guard inspects (§1.2) — a boundary that
/// moved for the sake of a completion list would move all three. Completion is the only consumer that wants
/// the looser reading, so it is the only one that gets it.
/// </para>
/// </summary>
public static class StatementStartHint
{
    /// <summary>
    /// True when the caret sits at the beginning of a line, with nothing but indentation and at most one
    /// bare word before it, and there is earlier content in the buffer.
    /// <list type="bullet">
    /// <item>Only at a line start, and only before a single word: mid-statement (<c>select a, b |</c>) and
    /// after a completed clause (<c>where x = |</c>) are untouched, so the suggestion list is not padded
    /// where the answer is already known.</item>
    /// <item>Earlier content is required because an empty buffer needs no help — the grammar already admits
    /// every statement keyword at the root, so this would only duplicate what is there.</item>
    /// <item>A word rather than nothing, because the whole point is to fire <i>while</i> typing. The
    /// splitter's own blank-line rule matches a token's full text against the keyword set, so it can only
    /// fire once <c>select</c> is complete — by which time the suggestion is no longer wanted.</item>
    /// </list>
    /// </summary>
    public static bool AtLineStart(string sql, int caret)
    {
        if (string.IsNullOrEmpty(sql)) return false;
        caret = caret < 0 ? 0 : caret > sql.Length ? sql.Length : caret;

        var lineStart = sql.LastIndexOf('\n', caret == 0 ? 0 : caret - 1) + 1;
        if (lineStart > caret) lineStart = caret;   // caret sits on the newline itself

        var sawWord = false;
        for (var i = lineStart; i < caret; i++)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                // Indentation is fine; whitespace after a word means the word is finished and something
                // else is being typed, which is a continuation rather than a statement start.
                if (sawWord) return false;
                continue;
            }
            // A bare word only. Anything else — a comma, a dot, an operator, a quote — means the line is
            // already saying something the grammar can answer better.
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
            sawWord = true;
        }

        return HasContentBefore(sql, lineStart);
    }

    private static bool HasContentBefore(string sql, int lineStart)
    {
        for (var i = 0; i < lineStart; i++)
            if (!char.IsWhiteSpace(sql[i])) return true;
        return false;
    }
}
