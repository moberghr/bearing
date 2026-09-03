using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// What the caret position means semantically, derived from the ANTLR rule c3 reports there.
/// </summary>
public enum CompletionIntent
{
    TablePosition,
    ColumnPosition,
    FunctionCall,
    Keyword,
}

/// <summary>
/// A lexer/parser pair over one buffer, in whatever grammar produced it. Typed as ANTLR's base
/// <see cref="Antlr4.Runtime.Parser"/> because that is all the two consumers need — antlr4-c3's
/// <c>CodeCompletionCore</c> takes a <see cref="Antlr4.Runtime.Parser"/>, and the keyword text comes off
/// <see cref="Recognizer{TSymbol,TATNInterpreter}.Vocabulary"/> — so the generated class name never
/// escapes the rules object that built it.
/// </summary>
/// <param name="ParseEntryRule">Invokes the grammar's whole-file rule (<c>root</c>, <c>tsql_file</c>, …).
/// A delegate rather than a method on this record because the entry rule's name is the one part of a
/// generated parser that no base class exposes.</param>
public sealed record SqlParse(Parser Parser, CommonTokenStream Tokens, Action ParseEntryRule)
{
    /// <summary>
    /// Walk the parser over the whole buffer so c3 has a populated ATN path to collect along, then rewind
    /// for the collection pass. The parse is *expected* to fail: completion runs against the half-typed
    /// statement under the caret, which is why the recovering error strategy is kept and the error
    /// listeners are gone.
    /// </summary>
    public void PrimeForCompletion()
    {
        try { ParseEntryRule(); } catch { /* partial SQL */ }
        Parser.Reset();
    }
}

/// <summary>
/// One engine's grammar, reduced to what completion asks of it: how to build a lexer and a parser, which
/// rule indices mean a table / column / function position, what antlr4-c3 is handed, and the token types
/// the engine branches on — <b>named by role, never by number</b>.
/// <para>
/// This is the seam <see cref="PgCompletionRules"/> was already most of the way to being: its own doc
/// calls itself "THE single place that knows PostgreSQL grammar rule/token numbers", but
/// <see cref="CompletionEngine"/> and <see cref="FromClauseExtractor"/> still reached for
/// <c>PostgreSQLParser.DOT</c>, <c>.FROM</c>, <c>.LATERAL_P</c> and a dozen more directly, so a second
/// grammar could not be substituted however well isolated the rest was. Everything downstream of here
/// works in terms of <see cref="CompletionIntent"/> and the role properties below, which is what lets one
/// engine serve both dialects rather than one engine per grammar.
/// </para>
/// <para>
/// A grammar that has no token for a role returns <see cref="TokenConstants.InvalidType"/>, which no real
/// token ever carries — so the engine's comparison simply never matches and the branch drops out. That is
/// the right answer for, say, T-SQL and <see cref="Lateral"/>: it spells that <c>APPLY</c>, and a
/// mis-mapped constant would silently claim a keyword the grammar never emits.
/// </para>
/// </summary>
public interface ISqlParseRules
{
    /// <summary>A fresh parser and token stream over <paramref name="sql"/>, error listeners removed —
    /// parsing partial SQL at a caret is the normal case, not a fault.</summary>
    SqlParse Parse(string sql);

    /// <summary>Lex the whole input into a filled token list (default channel + hidden), for the
    /// token-driven passes that never build a parse tree.</summary>
    IList<IToken> LexAll(string sql);

    /// <summary>Rules c3 is asked to stop at and report as candidates, outermost-first — see
    /// <see cref="PgCompletionRules.PreferredRules"/> for why the containing rule is the one listed.</summary>
    IReadOnlySet<int> PreferredRules { get; }

    /// <summary>Token types that are never useful candidates (whitespace, comments).</summary>
    IReadOnlySet<int> IgnoredTokens { get; }

    /// <summary>What a candidate rule index means to completion.</summary>
    CompletionIntent Classify(int ruleIndex);

    // ---- Token roles ----------------------------------------------------------------------------
    // The engine reads a token stream backwards from the caret to answer "is this an alias slot?",
    // "may a whole predicate start here?", "is a join keyword missing?" — questions the parse tree
    // cannot answer over half-typed SQL. Each is a *role*, so the answer is grammar-independent.

    /// <summary>The qualifier separator in <c>alias.column</c>.</summary>
    int Dot { get; }

    /// <summary>The optional alias introducer, <c>from film as f</c>.</summary>
    int As { get; }

    int From { get; }
    int Join { get; }

    /// <summary>Separates both source refs and select-list items.</summary>
    int Comma { get; }

    /// <summary>The join condition keyword — one of the places a whole predicate can begin.</summary>
    int On { get; }

    int Where { get; }
    int And { get; }
    int Or { get; }
    int Not { get; }
    int Having { get; }

    /// <summary>An opening parenthesis: it opens a boolean context, and it also marks a derived table,
    /// which is where the FROM scan stops reading a name.</summary>
    int OpenParen { get; }

    /// <summary>The lateral-subquery keyword, if the grammar has one — see the note on absent roles.</summary>
    int Lateral { get; }

    /// <summary>
    /// True for the token types that name a relation, a column or an alias — the bare and the delimited
    /// form both. It is a predicate rather than a pair of constants because the count is grammar's
    /// business: PostgreSQL has two (<c>Identifier</c>, <c>QuotedIdentifier</c>) and T-SQL has a third
    /// for <c>[bracketed]</c> names.
    /// </summary>
    bool IsIdentifier(int tokenType);

    /// <summary>
    /// Words that qualify a <c>JOIN</c> and still take an <c>ON</c> clause (<c>left</c>, <c>inner</c>,
    /// <c>full outer</c>, …). After one of these the FK-join suggestion inserts the missing
    /// <c>join</c> keyword and its predicate.
    /// </summary>
    IReadOnlySet<int> JoinQualifiers { get; }

    /// <summary>
    /// Join qualifiers that take <b>no</b> <c>ON</c> clause (<c>cross</c>, <c>natural</c>). An FK-equality
    /// suggestion has no valid shape after these, so it is withheld rather than inserted as a syntax error.
    /// </summary>
    IReadOnlySet<int> OnlessJoinQualifiers { get; }
}
