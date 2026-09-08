using System.Runtime.ExceptionServices;
using Antlr4.Runtime;

namespace Bearing.Sql;

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

    /// <summary>
    /// Stack for <see cref="OnDeepStack{T}"/>. Reserved address space, committed only as used, so a large
    /// number costs nothing until the recursion actually needs it.
    /// </summary>
    private const int DeepStackBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Run a parse (and any walk of its tree) on a thread with a stack sized for the grammar's recursion.
    /// <para>
    /// The generated parser is recursive descent and <c>a_expr</c> alone is some fifteen rule levels, so
    /// every level of bracket nesting costs a slice of stack. On a default 1 MB thread this grammar dies at
    /// about 200 levels of parentheses, and the tree walk on top of it at about 150 — which is low enough
    /// for machine-generated SQL to reach. Measured on 64 MB: depth 6400 completes, 12800 overflows.
    /// </para>
    /// <para>
    /// Worth doing rather than simply refusing, because the alternative is a limit the user can hit with
    /// real (if ugly) SQL. Cheap, too: a thread costs tens of microseconds against a parse measured in
    /// milliseconds, and completion is debounced and already off the UI thread.
    /// </para>
    /// </summary>
    public static T OnDeepStack<T>(Func<T> work)
    {
        var result = default(T)!;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(
            () =>
            {
                try { result = work(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            },
            DeepStackBytes)
        { IsBackground = true };

        thread.Start();
        thread.Join();
        failure?.Throw();   // rethrown on the caller's thread with the original stack trace
        return result;
    }

    /// <summary>
    /// The backstop against an uncatchable crash — <b>not</b> a functional limit on how large or complex a
    /// statement may be.
    /// <para>
    /// Size is not the constraint here and never was: a hundred-kilobyte script, a five-thousand-element
    /// <c>IN</c> list and a two-thousand-column select list all format in well under a second. Only
    /// <i>nesting depth</i> costs stack, and <see cref="OnDeepStack{T}"/> already buys about 6400 levels of
    /// it. This exists because past that the failure mode is a <see cref="StackOverflowException"/>, which
    /// .NET cannot catch: the process dies and takes the user's unsaved buffer with it. There is no
    /// "handle it anyway" option to prefer — the choice is between declining and crashing.
    /// </para>
    /// <para>
    /// 1000 sits about six times under the measured cliff and keeps the worst case under a second (depth
    /// 800 parses in ~600 ms, 1600 in ~1.4 s, 3200 in ~3.5 s — the parser is superlinear in depth, so past
    /// this point time binds before the stack does anyway). Real SQL does not approach it: the deepest
    /// thing in the test corpus is single digits.
    /// </para>
    /// </summary>
    public const int MaxNestingDepth = 1000;

    /// <summary>
    /// The deepest bracket / CASE nesting in the token stream. Counted iteratively over tokens, so it is
    /// safe on exactly the input the parser is not.
    /// </summary>
    public static int NestingDepth(IEnumerable<IToken> tokens)
    {
        var depth = 0;
        var deepest = 0;
        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case PostgreSQLLexer.OPEN_PAREN:
                case PostgreSQLLexer.OPEN_BRACKET:
                case PostgreSQLLexer.CASE:
                    if (++depth > deepest) deepest = depth;
                    break;
                case PostgreSQLLexer.CLOSE_PAREN:
                case PostgreSQLLexer.CLOSE_BRACKET:
                case PostgreSQLLexer.END_P:
                    if (depth > 0) depth--;   // unbalanced input is normal mid-edit; never go negative
                    break;
            }
        }
        return deepest;
    }

    /// <summary>Whether <paramref name="tokens"/> is nested too deeply to hand to the parser
    /// (<see cref="MaxNestingDepth"/>).</summary>
    public static bool TooDeeplyNested(IEnumerable<IToken> tokens) => NestingDepth(tokens) > MaxNestingDepth;

    /// <summary>The same, lexing the text first. Use the token overload where the caller has already
    /// filled a stream.</summary>
    public static bool TooDeeplyNested(string sql) => TooDeeplyNested(LexAll(sql));
}
