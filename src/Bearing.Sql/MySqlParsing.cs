using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// Thin factory over the ANTLR-generated MySQL lexer/parser (global namespace) — the twin of
/// <see cref="TSqlParsing"/>. The grammar is <c>caseInsensitive</c>, so no casing scaffolding; whitespace and
/// comments go to hidden channels (and <c>/*! … */</c> to its own), so a filled token list holds them too.
/// </summary>
public static class MySqlParsing
{
    public sealed record Parsed(MySqlParser Parser, CommonTokenStream Tokens);

    public static Parsed Create(string sql)
    {
        var lexer = new MySqlLexer(CharStreams.fromString(sql));
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        var parser = new MySqlParser(tokens);
        parser.RemoveErrorListeners();
        return new Parsed(parser, tokens);
    }

    /// <summary>Lex the whole input into a filled token list (every channel).</summary>
    public static IList<IToken> LexAll(string sql)
    {
        var lexer = new MySqlLexer(CharStreams.fromString(sql));
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        tokens.Fill();
        return tokens.GetTokens();
    }
}
