using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// PostgreSQL as an <see cref="ISqlParseRules"/>. Deliberately nothing but forwarding: the rule indices
/// and candidate sets stay in <see cref="PgCompletionRules"/> and the lexer/parser construction stays in
/// <see cref="PgParsing"/>, exactly the arrangement <see cref="PostgresDialect"/> has with
/// <see cref="PgIdentifier"/> and <see cref="PageSql"/>. One implementation of the Postgres rules, not
/// two — which is also the only way to be sure the seam did not move Postgres' answers.
/// <para>
/// The token roles below are the constants <see cref="CompletionEngine"/> and
/// <see cref="FromClauseExtractor"/> used to name inline. Nothing was re-chosen while moving them; if the
/// grammar is regenerated and numbering shifts, this file and <see cref="PgCompletionRules"/> are still
/// the only two that need to change.
/// </para>
/// </summary>
public sealed class PgParseRules : ISqlParseRules
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static PgParseRules Instance { get; } = new();

    /// <summary>The grammar's whole-file rule is <c>root</c>, which is what the completion pass primes
    /// the parser over before c3 collects.</summary>
    public SqlParse Parse(string sql)
    {
        var parsed = PgParsing.Create(sql);
        return new SqlParse(parsed.Parser, parsed.Tokens, () => parsed.Parser.root());
    }

    public IList<IToken> LexAll(string sql) => PgParsing.LexAll(sql);

    public IReadOnlySet<int> PreferredRules => PgCompletionRules.PreferredRules;
    public IReadOnlySet<int> IgnoredTokens => PgCompletionRules.IgnoredTokens;
    public CompletionIntent Classify(int ruleIndex) => PgCompletionRules.Classify(ruleIndex);

    public int Dot => PostgreSQLParser.DOT;
    public int As => PostgreSQLParser.AS;
    public int From => PostgreSQLParser.FROM;
    public int Join => PostgreSQLParser.JOIN;
    public int Comma => PostgreSQLParser.COMMA;
    public int On => PostgreSQLParser.ON;
    public int Where => PostgreSQLParser.WHERE;
    public int And => PostgreSQLParser.AND;
    public int Or => PostgreSQLParser.OR;
    public int Not => PostgreSQLParser.NOT;
    public int Having => PostgreSQLParser.HAVING;
    public int OpenParen => PostgreSQLParser.OPEN_PAREN;

    /// <summary><c>_P</c> is the grammar's suffix for a keyword that is also a C# / Java reserved word,
    /// not a different token — <c>LATERAL_P</c> is <c>lateral</c>.</summary>
    public int Lateral => PostgreSQLParser.LATERAL_P;

    public bool IsIdentifier(int tokenType)
        => tokenType is PostgreSQLParser.Identifier or PostgreSQLParser.QuotedIdentifier;

    public IReadOnlySet<int> JoinQualifiers { get; } = new HashSet<int>
    {
        PostgreSQLParser.LEFT,
        PostgreSQLParser.RIGHT,
        PostgreSQLParser.FULL,
        PostgreSQLParser.INNER_P,
        PostgreSQLParser.OUTER_P,
    };

    public IReadOnlySet<int> OnlessJoinQualifiers { get; } = new HashSet<int>
    {
        PostgreSQLParser.CROSS,
        PostgreSQLParser.NATURAL,
    };
}
