using System.Text.RegularExpressions;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// Transact-SQL as an <see cref="ISqlParseRules"/>, over the vendored <c>TSqlParser</c>. The other half
/// of the seam <see cref="PgParseRules"/> opened: one <see cref="CompletionEngine"/>, two grammars, and
/// no Postgres rule left standing in for T-SQL.
/// <para>
/// <b>The rule choice is the quality of T-SQL completion, so here is the whole of it.</b> antlr4-c3
/// reports only the <em>outermost</em> preferred rule on each path, and how far it stops short decides how
/// many raw token candidates come back with it — which on a ~700-rule grammar is the difference between a
/// readable popup and a keyword dump (§9.5). So an entry wants to be the rule that <em>contains</em> the
/// name. Every claim below is a measurement against this grammar, with the numbers, because reasoning from
/// the grammar text got one of them wrong the first time.
/// </para>
/// <list type="bullet">
/// <item><b><c>table_source</c> → a table position.</b> Not <c>full_table_name</c> and not
/// <c>table_source_item</c>. <c>table_sources</c> is the <c>FROM</c> list and <c>table_source</c> is one
/// entry of it (<c>table_source_item joins+=join_part*</c>), which is exactly the span a source occupies:
/// the name, its optional alias, its hints, and the joins hanging off it. It fires at <c>from |</c>,
/// <c>join |</c>, <c>cross apply |</c> and <c>from dbo.|</c>. Listing <c>full_table_name</c> instead
/// would also have claimed a table position for the <c>INTO</c> target of <c>select … into t</c> and for
/// the qualifier half of <c>full_column_name</c> — a column position dressed as a table one.</item>
/// <item><b><c>expression</c> → a column position.</b> The one that is not the obvious pick.
/// <c>full_column_name</c> is the direct analogue of Postgres' <c>columnref</c>, and it is <em>not</em>
/// true that it reports nothing on its own: re-measured across all six carets the tests below list,
/// <c>full_column_name</c> alone reports <c>ColumnPosition</c> at every one and the same columns come
/// back. What preferring the container actually buys is the <b>size of the candidate set</b>:
/// c3 stops at the preferred rule instead of enumerating every token that could follow it, so a caret
/// after <c>where</c> goes from <b>235</b> suggestions to <b>7</b> (three columns and four keywords),
/// <c>order by</c> from 231 to 3, <c>group by</c> from 232 to 5. That is the difference between a popup
/// the user reads and one they dismiss, and it is what
/// <c>TSqlCompletionTests.A_predicate_popup_is_short_and_column_led</c> pins. It also covers a function
/// argument (<c>select sum(|)</c>), which is the same ground Postgres' <c>columnref</c> covers —
/// everywhere an expression may stand — reached from the outside rather than the inside.</item>
/// <item><b><c>full_column_name</c> → a column position as well.</b> Kept because it is reachable where
/// <c>expression</c> is not, and measurably load-bearing: <c>update T set |</c> goes
/// <c>update_elem : (full_column_name | LOCAL_ID) …</c>, with no enclosing <c>expression</c>. Drop this
/// entry and that caret reports <b>no intent at all</b> and offers zero columns — 933 raw keyword
/// candidates and nothing else. It is the one caret the container rule cannot reach.</item>
/// <item><b>No function-call rule, deliberately.</b> T-SQL's <c>function_call</c> is an alternative
/// <em>of</em> <c>expression</c>, and in <c>table_source_item</c> it sits under <c>table_source</c> — so
/// with the two rules above preferred it is shadowed on every path and never surfaces (c3 reports the
/// outermost preferred rule only). Listing it would be a dead entry, and dropping <c>expression</c> to
/// make it live would buy the keyword flood back. <see cref="CompletionIntent.FunctionCall"/> therefore
/// never arises on T-SQL; <see cref="CompletionEngine"/> reads only the table and column intents, so
/// nothing downstream notices.</item>
/// </list>
/// <para>
/// What the choice costs, stated plainly. <c>table_source</c> also covers <c>APPLY</c> operands,
/// <c>OPENJSON</c> and a table-valued function, so a caret there is a "table position" and gets relations
/// offered — over-offering rather than mis-offering, and the same trade Postgres makes at
/// <c>table_ref</c>. And a <c>delete from |</c> caret reports nothing at all, because the grammar routes
/// <c>DELETE</c>'s target through <c>ddl_object</c> rather than <c>table_source</c>; that is a missing
/// suggestion in a statement nobody completes into by preference, not a wrong one.
/// </para>
/// </summary>
public sealed partial class TSqlParseRules : ISqlParseRules
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static TSqlParseRules Instance { get; } = new();

    /// <summary>The grammar's whole-file rule is <c>tsql_file</c> (<c>batch* EOF</c>), which is also rule
    /// index 0 — the rule antlr4-c3 starts its ATN walk from when it is handed no context, as the engine
    /// hands it none.</summary>
    public SqlParse Parse(string sql)
    {
        var parsed = TSqlParsing.Create(sql);
        return new SqlParse(parsed.Parser, parsed.Tokens, () => parsed.Parser.tsql_file());
    }

    public IList<IToken> LexAll(string sql) => TSqlParsing.LexAll(sql);

    public IReadOnlySet<int> PreferredRules { get; } = new HashSet<int>
    {
        TSqlParser.RULE_table_source,
        TSqlParser.RULE_expression,
        TSqlParser.RULE_full_column_name,
    };

    /// <summary>
    /// Whitespace and both comment kinds, the same three roles Postgres names. <c>SPACE</c> can never
    /// actually reach a candidate set — the T-SQL lexer <c>skip</c>s it rather than channelling it — but
    /// naming it keeps the set readable as "the tokens that are not language", and a future grammar bump
    /// that switches <c>skip</c> for <c>channel(HIDDEN)</c> does not silently start offering it.
    /// </summary>
    public IReadOnlySet<int> IgnoredTokens { get; } = new HashSet<int>
    {
        TSqlParser.SPACE,
        TSqlParser.COMMENT,
        TSqlParser.LINE_COMMENT,
    };

    public CompletionIntent Classify(int ruleIndex)
    {
        if (ruleIndex == TSqlParser.RULE_table_source) return CompletionIntent.TablePosition;
        if (ruleIndex == TSqlParser.RULE_expression) return CompletionIntent.ColumnPosition;
        if (ruleIndex == TSqlParser.RULE_full_column_name) return CompletionIntent.ColumnPosition;
        return CompletionIntent.Keyword;
    }

    // ---- Token roles ----------------------------------------------------------------------------

    public int Dot => TSqlParser.DOT;
    public int As => TSqlParser.AS;
    public int From => TSqlParser.FROM;
    public int Join => TSqlParser.JOIN;
    public int Comma => TSqlParser.COMMA;
    public int On => TSqlParser.ON;
    public int Where => TSqlParser.WHERE;
    public int And => TSqlParser.AND;
    public int Or => TSqlParser.OR;
    public int Not => TSqlParser.NOT;
    public int Having => TSqlParser.HAVING;
    public int OpenParen => TSqlParser.LR_BRACKET;

    /// <summary>
    /// Absent, as <see cref="ISqlParseRules"/> anticipates. T-SQL's nearest thing to <c>JOIN LATERAL</c>
    /// is <c>CROSS</c>/<c>OUTER APPLY</c>, and the roles do not line up: <c>LATERAL</c> is a prefix
    /// <em>after</em> <c>JOIN</c>, whereas <c>APPLY</c> <em>replaces</em> <c>JOIN</c> and follows its
    /// qualifier. Mapping this to <c>APPLY</c> would make the FROM scan skip a token that never sits where
    /// it looks, so it returns the invalid type and the branch drops out instead.
    /// </summary>
    public int Lateral => TokenConstants.InvalidType;

    /// <summary>
    /// The four identifier tokens of <c>id_</c> that actually name something: a bare <c>ID</c>, a
    /// <c>#temp</c> table (<c>TEMP_ID</c>), a <c>"quoted"</c> name and a <c>[bracketed]</c> one.
    /// <para>
    /// <c>id_</c> itself also admits <c>keyword</c> and <c>RAW</c>, and those are deliberately excluded:
    /// T-SQL lets most non-reserved keywords be used bare as a name, so accepting them here would make
    /// <c>from Orders where …</c> read <c>where</c> as <c>Orders</c>' alias and drop the WHERE clause out
    /// of the scan. Postgres' own <c>IsIdentifier</c> excludes its unreserved keywords for the same
    /// reason.
    /// </para>
    /// </summary>
    public bool IsIdentifier(int tokenType)
        => tokenType is TSqlParser.ID or TSqlParser.TEMP_ID
            or TSqlParser.DOUBLE_QUOTE_ID or TSqlParser.SQUARE_BRACKET_ID;

    public string Unquote(string identifier) => SqlServerIdentifier.Unquote(identifier);

    /// <summary>
    /// The Postgres pattern widened to T-SQL's delimiters: a <c>[bracketed]</c> name (with its
    /// <c>]]</c> escape), a <c>"quoted"</c> one, or a bare name — which in T-SQL may also carry
    /// <c>@</c>, <c>$</c> and <c>#</c> after the first character. The trailing alternation is the
    /// half-typed suffix, an unterminated <c>[</c> or <c>"</c> included, since the popup opens while the
    /// column name is still being typed.
    /// </summary>
    public string? QualifierBefore(string sql, int caret)
    {
        var m = QualifierDotRegex().Match(sql[..caret]);
        return m.Success ? SqlServerIdentifier.Unquote(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(
        @"(\[(?:[^\]]|\]\])*\]|""(?:[^""]|"""")*""|[A-Za-z_#][A-Za-z0-9_@$#]*)\s*\.\s*"
        + @"(?:\[(?:[^\]]|\]\])*|""(?:[^""]|"""")*|[A-Za-z0-9_@$#]*)$")]
    private static partial Regex QualifierDotRegex();

    /// <summary>
    /// T-SQL's <c>join_on</c> qualifiers: <c>INNER</c>, and <c>LEFT</c>/<c>RIGHT</c>/<c>FULL</c> with an
    /// optional <c>OUTER</c> — each of which still needs the <c>ON</c> the FK suggestion supplies.
    /// <c>OUTER</c> is the one impurity, since it also leads <c>OUTER APPLY</c>, which takes none; the
    /// same impurity Postgres' <c>OUTER_P</c> carries, and resolved the same way, because
    /// <c>left outer |</c> is overwhelmingly the reading and the cost of the other is one suggestion the
    /// user does not take.
    /// </summary>
    public IReadOnlySet<int> JoinQualifiers { get; } = new HashSet<int>
    {
        TSqlParser.LEFT,
        TSqlParser.RIGHT,
        TSqlParser.FULL,
        TSqlParser.INNER,
        TSqlParser.OUTER,
    };

    /// <summary>
    /// <c>CROSS</c> alone. It leads both of the grammar's ON-less forms — <c>CROSS JOIN</c> and
    /// <c>CROSS APPLY</c> — and there is no Postgres-style <c>NATURAL</c> join in T-SQL at all (the
    /// grammar has no such token), so the set is one element rather than two.
    /// </summary>
    public IReadOnlySet<int> OnlessJoinQualifiers { get; } = new HashSet<int>
    {
        TSqlParser.CROSS,
    };
}
