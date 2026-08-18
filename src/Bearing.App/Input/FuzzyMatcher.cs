namespace Bearing.App.Input;

/// <summary>
/// How well a query matched — the coarse bucket that decides ordering before any score does, so a
/// near-literal hit can never sink below a scattered one. Higher is better.
/// </summary>
public enum MatchQuality
{
    /// <summary>The query isn't even a subsequence of the text.</summary>
    None = 0,

    /// <summary>Matched, but the characters are scattered (<c>accli</c> in <c>accounting_lines</c>).</summary>
    Subsequence = 1,

    /// <summary>The query appears verbatim somewhere inside the text.</summary>
    Substring = 2,

    /// <summary>Every query character landed on a word start (<c>al</c> → <c>accounting_lines</c>).</summary>
    Initials = 3,

    /// <summary>The text starts with the query.</summary>
    Prefix = 4,

    /// <summary>The whole text is the query.</summary>
    Exact = 5,
}

/// <summary>
/// Fuzzy subsequence matching over a name: the query has to appear in order, contiguous runs and
/// word-start hits score higher, and an earlier first hit wins. Word starts are read across every
/// convention a SQL catalog or a command title uses — <c>snake_case</c>, <c>kebab-case</c>, dots,
/// spaces, and <c>camelCase</c>/<c>PascalCase</c> transitions.
/// <para>
/// Pure and UI-free: the command palette (<see cref="PaletteFilter"/>) and the completion popup
/// (<c>Completion.SuggestionRanker</c>) both rank with it, and both are testable without a window.
/// </para>
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>A match, or <see cref="MatchQuality.None"/> when <paramref name="query"/> isn't a
    /// subsequence of <paramref name="text"/>. <see cref="Score"/> is only comparable within a quality.</summary>
    public readonly record struct Result(MatchQuality Quality, int Score)
    {
        public static readonly Result NoMatch = new(MatchQuality.None, 0);
        public bool IsMatch => Quality != MatchQuality.None;
    }

    public static Result Match(string text, string query)
    {
        if (query.Length == 0) return new Result(MatchQuality.Prefix, 0);
        if (text.Length == 0) return Result.NoMatch;

        var lowerText = text.ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();

        var greedy = Scan(text, lowerText, lowerQuery, wordStartsOnly: false);
        if (greedy is null) return Result.NoMatch;

        // The leftmost-greedy walk can miss the initials reading: "ws" over warehouse_shipments takes
        // the 's' inside "warehouse" and never reaches "shipments". A second pass restricted to word
        // starts finds it, and scores it the way initials deserve.
        var initials = Scan(text, lowerText, lowerQuery, wordStartsOnly: true);

        var quality =
            lowerText.Equals(lowerQuery, StringComparison.Ordinal) ? MatchQuality.Exact
            : lowerText.StartsWith(lowerQuery, StringComparison.Ordinal) ? MatchQuality.Prefix
            : initials is not null ? MatchQuality.Initials
            : lowerText.Contains(lowerQuery, StringComparison.Ordinal) ? MatchQuality.Substring
            : MatchQuality.Subsequence;

        return new Result(quality, Math.Max(greedy.Value, initials ?? int.MinValue));
    }

    /// <summary>
    /// Walk <paramref name="lowerQuery"/> through <paramref name="lowerText"/> left to right, scoring
    /// contiguous runs (+5) and word starts (+3) and penalising a late first hit. Returns null when the
    /// query doesn't fit — which for <paramref name="wordStartsOnly"/> means "these aren't its initials".
    /// </summary>
    private static int? Scan(string text, string lowerText, string lowerQuery, bool wordStartsOnly)
    {
        var from = 0;
        var score = 0;
        var firstIdx = -1;
        var prevIdx = -2;
        foreach (var c in lowerQuery)
        {
            var idx = IndexOf(text, lowerText, c, from, wordStartsOnly);
            if (idx < 0) return null;
            if (firstIdx < 0) firstIdx = idx;
            if (idx == prevIdx + 1) score += 5;                        // contiguous run
            if (IsWordStart(text, idx)) score += 3;
            prevIdx = idx;
            from = idx + 1;
        }
        return score - firstIdx;                                       // earlier first hit is better
    }

    private static int IndexOf(string text, string lowerText, char c, int from, bool wordStartsOnly)
    {
        for (var i = from; i < lowerText.Length; i++)
            if (lowerText[i] == c && (!wordStartsOnly || IsWordStart(text, i)))
                return i;
        return -1;
    }

    /// <summary>
    /// True when position <paramref name="index"/> begins a word: the start of the text, anything after
    /// a separator, or a lower→upper transition. The case test reads the <em>original</em> text — the
    /// scorer works on a lower-cased copy, which is exactly why <c>al</c> used to match
    /// <c>AccountingLines</c> without earning the bonus and sank below noise.
    /// </summary>
    public static bool IsWordStart(string text, int index)
    {
        if (index == 0) return true;
        var prev = text[index - 1];
        if (!char.IsLetterOrDigit(prev)) return true;                  // '_', '-', '.', space …
        return char.IsUpper(text[index]) && !char.IsUpper(prev);
    }
}
