using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// A CTE statement cut in two at the boundary the grammar reports: the <c>with</c> preamble, and the
/// query it introduces.
/// </summary>
/// <param name="With">Everything up to the body's first token — the <c>WITH</c> keyword, every CTE
/// definition, and whatever whitespace or comments sat between them. Kept verbatim rather than
/// re-rendered, so nothing the user wrote is lost or moved.</param>
/// <param name="Body">The outer query, trailing semicolon included if it had one.</param>
public sealed record TSqlCteSplit(string With, string Body);

/// <summary>
/// Finds where a T-SQL statement's CTE list ends and its outer query begins, so a paged query can hoist
/// the CTEs out of the derived table it cannot legally sit in:
/// <c>with c as (…) select * from (&lt;body&gt;) as _sq order by (select null) offset … fetch …</c>.
/// <para>
/// <b>Why this needs the parse tree.</b> The boundary cannot be found by counting parentheses or looking
/// for the next <c>SELECT</c>: a CTE body is itself a whole <c>select_statement</c>, so every keyword the
/// outer query starts with also appears inside the CTEs, and a CTE may carry a column list, nested CTEs
/// in a derived table, comments, and a name that is a bracketed keyword. The grammar states the boundary
/// outright — <c>select_statement_standalone : with_expression? select_statement</c> — so the first token
/// of the <c>select_statement</c> child <em>is</em> the answer, with no heuristic in between.
/// </para>
/// <para>
/// <b>Why it refuses so readily.</b> A mis-placed cut produces SQL the server accepts and answers
/// <em>wrongly</em> — a silently different result set, which is worse than a query that simply cannot be
/// paged. So every one of these is a refusal, not a best effort: more than one statement in the buffer,
/// a leading word that is not <c>WITH</c>, a single syntax error anywhere, a parse that does not reach
/// EOF, or a shape that parses as something other than "CTEs then a SELECT" (a CTE feeding an
/// <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>/<c>MERGE</c> is the common one, and is a write besides).
/// The caller then keeps the old behaviour: it retires paging and says so.
/// </para>
/// </summary>
public static class TSqlCteSplitter
{
    /// <summary>
    /// <paramref name="sql"/> cut at its CTE boundary, or null when it is not a lone CTE-led
    /// <c>SELECT</c> that parses cleanly end to end.
    /// </summary>
    public static TSqlCteSplit? Split(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;

        // One statement only — a batch's second statement would take the hoisted preamble with it, and
        // `GO` is not something the server would even see. Split with the T-SQL scanner so a semicolon
        // inside a [delimited name] or a string does not count as a boundary.
        var statements = TSqlScanner.Split(sql);
        if (statements.Count != 1) return null;

        // …and that one statement has to be the whole buffer. The splitter drops a `GO`, so
        // `with c as (…) select * from c` followed by a lone `GO` counts as one statement — while the
        // parser, whose `id_` admits most keywords, happily reads that `GO` as the body's table alias
        // (`from c GO`) and reports no error at all. Without this the separator would ride into the
        // derived table. A trailing semicolon is the one thing allowed outside the span, since the
        // splitter leaves it out by construction.
        var stmt = statements[0];
        var tail = sql[(stmt.Start + stmt.Text.Length)..].Trim();
        if (sql[..stmt.Start].Trim().Length > 0 || (tail.Length > 0 && tail != ";")) return null;

        // Cheap gate before the expensive one: no leading WITH, nothing to hoist. Also keeps the whole
        // T-SQL parser off the hot path for the ordinary `select * from T` the pager sees most.
        var words = TSqlScanner.TopLevelWords(stmt.Tokens);
        if (words.Count == 0 || !string.Equals(words[0], "WITH", StringComparison.OrdinalIgnoreCase))
            return null;

        var parsed = TSqlParsing.Create(sql);
        var errors = new SyntaxErrorCounter();
        parsed.Parser.AddErrorListener(errors);

        // ANTLR's recovering strategy normally returns a tree rather than throwing, but it is not a
        // promise — and this runs on the paging path, where an escaping exception would break a query the
        // user can already see instead of quietly declining to page it. Refusing is the failure mode
        // this whole function is built around, so a throw joins the other refusals.
        TSqlParser.Select_statement_standaloneContext standalone;
        try { standalone = parsed.Parser.select_statement_standalone(); }
        catch { return null; }

        // Any syntax error at all disqualifies the cut: ANTLR's default strategy recovers, so a tree
        // comes back either way, and a recovered tree's token offsets are exactly where a wrong boundary
        // would come from.
        if (errors.Count > 0) return null;

        // The whole buffer has to have been consumed. Without this, `with c as (…) select * from c junk`
        // could hand back a body that silently drops `junk`.
        if (parsed.Parser.CurrentToken.Type != TokenConstants.EOF) return null;

        if (standalone.with_expression() is null) return null;
        if (standalone.select_statement() is not { Start: { } bodyStart }) return null;

        var cut = bodyStart.StartIndex;
        if (cut <= 0 || cut >= sql.Length) return null;

        return new TSqlCteSplit(sql[..cut].TrimEnd(), sql[cut..]);
    }

    /// <summary>Counts syntax errors instead of writing them anywhere. <c>TSqlParsing.Create</c> strips
    /// the console listener because a half-typed caret parse is normal; here a parse error is the signal
    /// that the boundary cannot be trusted, so it has to be observed rather than discarded.</summary>
    private sealed class SyntaxErrorCounter : IAntlrErrorListener<IToken>
    {
        public int Count { get; private set; }

        public void SyntaxError(
            TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line,
            int charPositionInLine, string msg, RecognitionException e) => Count++;
    }
}
