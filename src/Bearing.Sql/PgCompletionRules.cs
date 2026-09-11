using System.Linq;

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

        // The write statements name their target through their own rules, not through table_ref — which is
        // the FROM-clause rule and nothing else. Without these, `update `, `insert into ` and `delete from `
        // reported no table position at all and offered keywords only.
        PostgreSQLParser.RULE_relation_expr_opt_alias,   // UPDATE t / DELETE FROM t
        PostgreSQLParser.RULE_insert_target,             // INSERT INTO t

        // Likewise the column positions a write statement has, which are not columnref either.
        PostgreSQLParser.RULE_set_target,                // UPDATE t SET c = …
        PostgreSQLParser.RULE_insert_column_item,        // INSERT INTO t (c, …)
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
        if (ruleIndex == PostgreSQLParser.RULE_table_ref
            || ruleIndex == PostgreSQLParser.RULE_relation_expr_opt_alias
            || ruleIndex == PostgreSQLParser.RULE_insert_target) return CompletionIntent.TablePosition;

        if (ruleIndex == PostgreSQLParser.RULE_columnref
            || ruleIndex == PostgreSQLParser.RULE_set_target
            || ruleIndex == PostgreSQLParser.RULE_insert_column_item) return CompletionIntent.ColumnPosition;

        if (ruleIndex == PostgreSQLParser.RULE_func_name) return CompletionIntent.FunctionCall;
        return CompletionIntent.Keyword;
    }

    /// <summary>
    /// Whether the caret is at a column slot belonging to a write statement's <b>target</b> — an
    /// <c>UPDATE … SET</c> or an insert column list — rather than a general expression position.
    /// <para>
    /// Both are <see cref="CompletionIntent.ColumnPosition"/>, but they are far stricter than one: only the
    /// target's own columns are legal there (<c>UPDATE users SET … FROM orders</c> reads two relations and
    /// may assign to exactly one), and the name must be bare — <c>UPDATE users u SET u.name = …</c> is an
    /// error, not a style. The intent alone cannot carry that, so callers ask this as well.
    /// </para>
    /// </summary>
    public static bool IsWriteTargetColumn(IEnumerable<int> ruleIndices)
        => ruleIndices.Any(r => r == PostgreSQLParser.RULE_set_target
                                || r == PostgreSQLParser.RULE_insert_column_item);

    /// <summary>
    /// Whether the caret names an INSERT's target relation. Unlike every other table position, an alias
    /// there needs <c>AS</c>: Postgres' grammar is <c>INSERT INTO t [ AS alias ]</c>, so the bare
    /// <c>users u</c> that suits a FROM clause is a syntax error here.
    /// </summary>
    public static bool IsInsertTarget(IEnumerable<int> ruleIndices)
        => ruleIndices.Any(r => r == PostgreSQLParser.RULE_insert_target);
}
