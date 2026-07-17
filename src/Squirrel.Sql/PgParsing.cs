using Antlr4.Runtime;

namespace Squirrel.Sql;

/// <summary>
/// Thin factory over the ANTLR-generated PostgreSQL lexer/parser (which live in the global
/// namespace). Parsing partial/invalid SQL at the caret is normal, so error listeners are
/// removed and the default (recovering) error strategy is kept.
/// </summary>
public static class PgParsing
{
    public sealed record Parsed(PostgreSQLParser Parser, CommonTokenStream Tokens);

    public static Parsed Create(string sql)
    {
        var input = CharStreams.fromString(sql);
        var lexer = new PostgreSQLLexer(input);
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        var parser = new PostgreSQLParser(tokens);
        parser.RemoveErrorListeners();
        return new Parsed(parser, tokens);
    }

    /// <summary>Lex the whole input into a filled token list (default channel + hidden).</summary>
    public static IList<IToken> LexAll(string sql)
    {
        var input = CharStreams.fromString(sql);
        var lexer = new PostgreSQLLexer(input);
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        tokens.Fill();
        return tokens.GetTokens();
    }
}
