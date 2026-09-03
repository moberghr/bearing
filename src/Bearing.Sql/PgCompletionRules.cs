namespace Bearing.Sql;

/// <summary>
/// THE single place that knows PostgreSQL grammar rule/token numbers. antlr4-c3 hands back
/// candidate *rule indices* and *token types*; this maps the interesting ones to intent.
///
/// If the grammar is regenerated and numbering shifts, only this file (and its pinning tests)
/// should need to change — everything downstream works in terms of <see cref="CompletionIntent"/>.
/// </summary>
public static class PgCompletionRules
{
    /// <summary>
    /// Rules we ask c3 to stop at and report as candidates. c3 reports the *outermost* preferred
    /// rule on the stack, so listing <c>table_ref</c> (not the inner <c>qualified_name</c>) keeps
    /// FROM-clause names unambiguous.
    /// </summary>
    public static readonly IReadOnlySet<int> PreferredRules = new HashSet<int>
    {
        PostgreSQLParser.RULE_table_ref,
        PostgreSQLParser.RULE_columnref,
        PostgreSQLParser.RULE_func_name,
    };

    /// <summary>Token types that are never useful completion candidates (whitespace/comments).</summary>
    public static readonly IReadOnlySet<int> IgnoredTokens = new HashSet<int>
    {
        PostgreSQLParser.Whitespace,
        PostgreSQLParser.Newline,
        PostgreSQLParser.LineComment,
        PostgreSQLParser.BlockComment,
    };

    public static CompletionIntent Classify(int ruleIndex)
    {
        if (ruleIndex == PostgreSQLParser.RULE_table_ref) return CompletionIntent.TablePosition;
        if (ruleIndex == PostgreSQLParser.RULE_columnref) return CompletionIntent.ColumnPosition;
        if (ruleIndex == PostgreSQLParser.RULE_func_name) return CompletionIntent.FunctionCall;
        return CompletionIntent.Keyword;
    }
}
