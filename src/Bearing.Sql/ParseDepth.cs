using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// How deeply a token stream nests, counted in whatever grammar produced it — the backstop that decides
/// whether a buffer may be handed to a recursive-descent parser at all.
/// <para>
/// Iterative over tokens on purpose, so it is safe on exactly the input the parser is not. It reads the
/// opener / closer sets off <see cref="ISqlParseRules"/> because token numbers are per grammar: the same
/// count taken with PostgreSQL's constants over a T-SQL stream counts unrelated keywords and answers ~0
/// for any nesting, which turns the guard into no guard — and the failure it guards against is an
/// uncatchable <see cref="StackOverflowException"/> that takes the user's unsaved buffer with it.
/// </para>
/// <para>
/// The limit itself is <see cref="PgParsing.MaxNestingDepth"/>: one number for both grammars, measured on
/// Postgres and verified to sit under T-SQL's cliff by <c>TSqlDepthGuardTests</c> rather than assumed to.
/// </para>
/// </summary>
public static class ParseDepth
{
    /// <summary>The deepest nesting reached anywhere in <paramref name="tokens"/>.</summary>
    public static int Of(ISqlParseRules rules, IEnumerable<IToken> tokens)
    {
        var openers = rules.NestOpeners;
        var closers = rules.NestClosers;

        var depth = 0;
        var deepest = 0;
        foreach (var token in tokens)
        {
            if (openers.Contains(token.Type))
            {
                if (++depth > deepest) deepest = depth;
            }
            else if (closers.Contains(token.Type) && depth > 0)
            {
                depth--;   // unbalanced input is normal mid-edit; never go negative
            }
        }
        return deepest;
    }

    /// <summary>Whether <paramref name="tokens"/> nests too deeply to hand to the parser
    /// (<see cref="PgParsing.MaxNestingDepth"/>).</summary>
    public static bool TooDeep(ISqlParseRules rules, IEnumerable<IToken> tokens)
        => Of(rules, tokens) > PgParsing.MaxNestingDepth;
}
