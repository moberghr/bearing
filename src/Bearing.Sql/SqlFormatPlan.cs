namespace Bearing.Sql;

/// <summary>
/// What goes in front of a token when the formatter writes it out.
/// <para>
/// <see cref="Keep"/> is the one that makes the whole design safe: a statement the layout pass has no
/// rules for keeps every byte of its original whitespace, so "we didn't understand it" comes out as "we
/// didn't touch it" rather than as a statement flattened onto one line.
/// </para>
/// </summary>
internal enum Gap
{
    /// <summary>Reproduce the source text between the previous token and this one, verbatim. The default
    /// outside a statement the layout pass manages.</summary>
    Keep,

    /// <summary>The writer decides from the source: a single space where the two tokens were separated by
    /// anything, nothing where they were already adjacent. This is what preserves <c>a::text</c>,
    /// <c>count(*)</c> and <c>f(x)</c> without a table of spacing exceptions — the user already wrote them
    /// correctly, and collapsing a run of whitespace can never merge two tokens.</summary>
    Auto,

    /// <summary>Butt against the previous token. Used before <c>,</c> and <c>;</c>, which cannot merge with
    /// anything that precedes them.</summary>
    None,

    Space,

    /// <summary>Newline, then the indent width × the token's indent level.</summary>
    Line,

    /// <summary>A blank line, then the indent. Between statements of a batch.</summary>
    Blank,
}

/// <summary>
/// The layout decision for every token of the input, indexed by token index — the whole output of the
/// tree walk, and the only thing the writer reads. Keeping it a flat array of plain data (rather than the
/// writer walking the tree itself) is what lets the layout rules and the rendering be tested apart.
/// </summary>
internal sealed class SqlFormatPlan
{
    private readonly Gap[] _gaps;
    private readonly int[] _indent;
    private readonly bool[] _managed;
    private readonly bool[] _keepCase;

    public SqlFormatPlan(int tokenCount)
    {
        _gaps = new Gap[tokenCount];
        _indent = new int[tokenCount];
        _managed = new bool[tokenCount];
        _keepCase = new bool[tokenCount];
        // Untouched by default: a token nothing claimed keeps its original surroundings.
        for (var i = 0; i < tokenCount; i++) _gaps[i] = Gap.Keep;
    }

    public int Count => _gaps.Length;

    public Gap GapBefore(int tokenIndex) => _gaps[tokenIndex];

    public int IndentAt(int tokenIndex) => _indent[tokenIndex];

    /// <summary>Whether this token sits inside a statement the layout pass understood. Managed tokens are
    /// re-laid-out and may be uppercased; the rest are copied through.</summary>
    public bool IsManaged(int tokenIndex) => _managed[tokenIndex];

    /// <summary>Mark a token as belonging to a statement being re-laid-out. Deliberately does not touch the
    /// indent: that only matters for a <see cref="Gap.Line"/>, and every break is set explicitly with the
    /// level it belongs at — so an outer rule claiming a range cannot flatten an inner rule's nesting.</summary>
    public void Manage(int tokenIndex)
    {
        _managed[tokenIndex] = true;
        // Managed tokens default to Auto rather than Keep: inside a statement being re-laid-out, the
        // original newlines are exactly what we are replacing.
        if (_gaps[tokenIndex] == Gap.Keep) _gaps[tokenIndex] = Gap.Auto;
    }

    /// <summary>
    /// Whether this token is a keyword being used as an <i>identifier</i> — a column called <c>name</c>, a
    /// function called <c>left</c> — and so must keep the case the user typed.
    /// <para>
    /// Postgres has some four hundred keywords and many of the non-reserved ones are ordinary column names,
    /// so uppercasing on the word alone turns <c>select value from t</c> into <c>select VALUE from t</c>.
    /// The grammar already draws the line — <c>colid: identifier | unreserved_keyword | col_name_keyword</c>
    /// — so a keyword reached through one of those rules is in an identifier position, and reaching it any
    /// other way means it is a real keyword.
    /// </para>
    /// </summary>
    public bool KeepsCase(int tokenIndex) => _keepCase[tokenIndex];

    public void KeepCase(int tokenIndex) => _keepCase[tokenIndex] = true;

    /// <summary>Set the break in front of a token. Later rules win, which is what lets an outer rule set a
    /// default and an inner one sharpen it.</summary>
    public void Set(int tokenIndex, Gap gap, int indent)
    {
        if (tokenIndex < 0 || tokenIndex >= _gaps.Length) return;
        _gaps[tokenIndex] = gap;
        _indent[tokenIndex] = indent;
    }
}
