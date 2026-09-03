using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// Thin factory over the ANTLR-generated T-SQL lexer/parser (which live in the global namespace) — the
/// twin of <see cref="PgParsing"/>, and deliberately as small. Parsing partial/invalid SQL at the caret
/// is the normal case, so error listeners are removed and the default (recovering) error strategy is kept.
/// <para>
/// Two differences from the PostgreSQL side, both the grammar's doing rather than a choice made here:
/// the lexer declares <c>caseInsensitive = true</c>, so <c>SELECT</c> / <c>select</c> / <c>Select</c>
/// arrive as one token type with no casing scaffolding; and whitespace is <c>-&gt; skip</c>ped rather
/// than pushed to the hidden channel, so a filled T-SQL token list holds words, punctuation and
/// comments only. Nothing downstream depends on whitespace being present — the caret walk in
/// <see cref="CompletionEngine"/> skips the hidden channel anyway — but it is why
/// <see cref="TSqlParseRules.IgnoredTokens"/> lists a token that can never actually be a candidate.
/// </para>
/// </summary>
public static class TSqlParsing
{
    public sealed record Parsed(TSqlParser Parser, CommonTokenStream Tokens);

    public static Parsed Create(string sql)
    {
        var input = CharStreams.fromString(sql);
        var lexer = new TSqlLexer(input);
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        var parser = new TSqlParser(tokens);
        parser.RemoveErrorListeners();
        return new Parsed(parser, tokens);
    }

    /// <summary>Lex the whole input into a filled token list (default channel + hidden).</summary>
    public static IList<IToken> LexAll(string sql)
    {
        var input = CharStreams.fromString(sql);
        var lexer = new TSqlLexer(input);
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        tokens.Fill();
        return tokens.GetTokens();
    }
}
