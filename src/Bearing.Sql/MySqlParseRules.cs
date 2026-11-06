using System.Text.RegularExpressions;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// MySQL / MariaDB as an <see cref="ISqlParseRules"/>, over the vendored <c>MySqlParser</c>
/// (grammars-v4 <c>sql/mysql/Positive-Technologies</c>, with the two local lexer edits its README lists).
/// One grammar serves both engines: completion needs the shared core — sources, joins, expressions — and
/// MariaDB's additions are clauses the parser recovers over.
/// <para>
/// <b>The rule choice</b>, by the same reasoning <see cref="TSqlParseRules"/> spells out (c3 reports the
/// outermost preferred rule on each path, so an entry should be the rule that <em>contains</em> the name):
/// </para>
/// <list type="bullet">
///   <item><c>tableSource</c> → a table position: one FROM/UPDATE entry with its alias and joins.</item>
///   <item><c>tableName</c> → a table position too, for the targets that do not go through
///     <c>tableSource</c>: <c>INSERT INTO</c>, <c>REPLACE INTO</c>, single-table <c>DELETE FROM</c>, and the
///     DDL verbs. Inside a FROM clause it sits under <c>tableSource</c> and is shadowed, so it only ever
///     surfaces at those targets — which is also what makes it the alias-less answer below.</item>
///   <item><c>expression</c> → a column position: the container, so a predicate popup stays short.</item>
///   <item><c>fullColumnName</c> → a column position where no <c>expression</c> encloses it:
///     <c>UPDATE t SET |</c>, <c>INSERT … SET |</c>, <c>ON DUPLICATE KEY UPDATE |</c> and an insert column
///     list. Those are exactly the write-target slots, which is how <see cref="IsWriteTargetColumn"/> can
///     tell them apart from a general expression with rule indices alone.</item>
/// </list>
/// </summary>
public sealed partial class MySqlParseRules : ISqlParseRules
{
    public static MySqlParseRules Instance { get; } = new();

    /// <summary>The grammar's whole-file rule is <c>root</c> (<c>sqlStatements? EOF</c>), rule index 0.</summary>
    public SqlParse Parse(string sql)
    {
        var parsed = MySqlParsing.Create(sql);
        return new SqlParse(parsed.Parser, parsed.Tokens, () => parsed.Parser.root());
    }

    public IList<IToken> LexAll(string sql) => MySqlParsing.LexAll(sql);

    public IReadOnlySet<int> PreferredRules { get; } = new HashSet<int>
    {
        MySqlParser.RULE_tableSource,
        MySqlParser.RULE_tableName,
        MySqlParser.RULE_expression,
        MySqlParser.RULE_fullColumnName,
    };

    /// <summary>Whitespace and the three comment forms — <c>/*! … */</c> included, which the lexer puts on
    /// a channel of its own.</summary>
    public IReadOnlySet<int> IgnoredTokens { get; } = new HashSet<int>
    {
        MySqlParser.SPACE,
        MySqlParser.SPEC_MYSQL_COMMENT,
        MySqlParser.COMMENT_INPUT,
        MySqlParser.LINE_COMMENT,
    };

    public CompletionIntent Classify(int ruleIndex)
    {
        if (ruleIndex == MySqlParser.RULE_tableSource || ruleIndex == MySqlParser.RULE_tableName)
        {
            return CompletionIntent.TablePosition;
        }
        if (ruleIndex == MySqlParser.RULE_expression || ruleIndex == MySqlParser.RULE_fullColumnName)
        {
            return CompletionIntent.ColumnPosition;
        }
        return CompletionIntent.Keyword;
    }

    // ---- Token roles ----------------------------------------------------------------------------

    public int Dot => MySqlParser.DOT;
    public int As => MySqlParser.AS;
    public int From => MySqlParser.FROM;
    public int Join => MySqlParser.JOIN;
    public int Comma => MySqlParser.COMMA;
    public int On => MySqlParser.ON;
    public int Where => MySqlParser.WHERE;
    public int And => MySqlParser.AND;
    public int Or => MySqlParser.OR;
    public int Not => MySqlParser.NOT;
    public int Having => MySqlParser.HAVING;
    public int OpenParen => MySqlParser.LR_BRACKET;

    /// <summary><c>JOIN LATERAL</c> — MySQL 8.0.14's, in the same position as Postgres'.</summary>
    public int Lateral => MySqlParser.LATERAL;

    /// <summary>Parentheses, <c>CASE</c> and <c>BEGIN</c>, closed by <c>)</c> and <c>END</c> — the same
    /// shape as T-SQL's, for the same reason: a stored-program body is a level the parser recurses
    /// through. Over-counts <c>BEGIN WORK</c>, which is the safe direction for a crash backstop.</summary>
    public IReadOnlySet<int> NestOpeners { get; } = new HashSet<int>
    {
        MySqlParser.LR_BRACKET,
        MySqlParser.CASE,
        MySqlParser.BEGIN,
    };

    /// <inheritdoc />
    public IReadOnlySet<int> NestClosers { get; } = new HashSet<int>
    {
        MySqlParser.RR_BRACKET,
        MySqlParser.END,
    };

    public int Using => MySqlParser.USING;
    public int Update => MySqlParser.UPDATE;
    public int Into => MySqlParser.INTO;
    public int Insert => MySqlParser.INSERT;

    /// <summary>Absent: neither engine has <c>MERGE</c>. MySQL's upsert is
    /// <c>INSERT … ON DUPLICATE KEY UPDATE</c>.</summary>
    public int Merge => TokenConstants.InvalidType;

    public int For => MySqlParser.FOR;

    /// <summary>Absent: MySQL's <c>DO</c> is a statement that evaluates expressions, never
    /// <c>DO UPDATE</c>.</summary>
    public int Do => TokenConstants.InvalidType;

    /// <summary>Absent: no inheritance, so no <c>ONLY</c>.</summary>
    public int Only => TokenConstants.InvalidType;

    /// <summary>A column slot that only <c>fullColumnName</c> reaches — see the type's remarks. Both
    /// reported means the caret is ambiguous between a write slot and an expression, and the general
    /// answer is the one that cannot hide columns.</summary>
    public bool IsWriteTargetColumn(IEnumerable<int> ruleIndices)
    {
        var rules = ruleIndices as ICollection<int> ?? ruleIndices.ToList();
        return rules.Contains(MySqlParser.RULE_fullColumnName) && !rules.Contains(MySqlParser.RULE_expression);
    }

    /// <summary>
    /// A table name reported on its own (outside a <c>tableSource</c>): <c>INSERT INTO t</c> and
    /// <c>REPLACE INTO t</c> take no alias at all, MariaDB's single-table <c>DELETE FROM t</c> takes none
    /// either (MySQL admits one from 8.0.16, so withholding it costs MySQL a suggestion, not an error), and
    /// DDL targets never do. <c>UPDATE t u SET …</c> is legal and goes through <c>tableSource</c>, so it
    /// keeps its alias.
    /// </summary>
    public bool IsAliaslessWriteTarget(IEnumerable<int> ruleIndices)
        => ruleIndices.Any(r => r == MySqlParser.RULE_tableName);

    /// <summary>A bare <c>ID</c> or a backticked name. Keywords usable as names (<c>keywordsCanBeId</c>)
    /// are excluded, as on the other dialects, so <c>from t where</c> does not read <c>where</c> as an
    /// alias.</summary>
    public bool IsIdentifier(int tokenType)
        => tokenType is MySqlParser.ID or MySqlParser.REVERSE_QUOTE_ID;

    public string Unquote(string identifier) => MySqlIdentifier.Unquote(identifier);

    /// <summary>A backticked name (with its <c>``</c> escape) or a bare one, then a dot, then the
    /// half-typed suffix — an unterminated backtick included.</summary>
    public string? QualifierBefore(string sql, int caret)
    {
        var m = QualifierDotRegex().Match(sql[..caret]);
        return m.Success ? MySqlIdentifier.Unquote(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(
        @"(`(?:[^`]|``)*`|[A-Za-z_$\u0080-￿][A-Za-z0-9_$\u0080-￿]*)\s*\.\s*"
        + @"(?:`(?:[^`]|``)*|[A-Za-z0-9_$\u0080-￿]*)$")]
    private static partial Regex QualifierDotRegex();

    /// <summary><c>LEFT</c>/<c>RIGHT</c> (with an optional <c>OUTER</c>) and <c>INNER</c> — each still
    /// needs the <c>ON</c> the FK suggestion supplies.</summary>
    public IReadOnlySet<int> JoinQualifiers { get; } = new HashSet<int>
    {
        MySqlParser.LEFT,
        MySqlParser.RIGHT,
        MySqlParser.INNER,
        MySqlParser.OUTER,
    };

    /// <summary><c>NATURAL</c> takes no <c>ON</c>. <c>CROSS</c> is listed too: MySQL tolerates
    /// <c>CROSS JOIN … ON</c>, but a cross join with an FK predicate is an inner join written misleadingly,
    /// and the other dialects withhold it there as well.</summary>
    public IReadOnlySet<int> OnlessJoinQualifiers { get; } = new HashSet<int>
    {
        MySqlParser.CROSS,
        MySqlParser.NATURAL,
    };
}
