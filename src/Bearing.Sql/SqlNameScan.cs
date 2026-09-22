using System;
using System.Collections.Generic;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// The names a statement mentions, read off its <b>tokens</b> — which identifiers it calls as functions, and
/// which it names at all. Used by <see cref="ExternalSqlPolicy"/> to apply the per-engine denied lists
/// (§1.11b).
/// <para>
/// <b>Lexer-based because the regex it replaced was evadable with four characters.</b> The old scan matched
/// <c>\b&lt;name&gt;["\]]?\s*\(</c>, and a SQL comment is whitespace to the engine but not to <c>\s</c>:
/// <c>select pg_read_file/**/('/etc/passwd')</c> is valid Postgres, is a plain SELECT the allow-list admits,
/// and skipped the list entirely. That is not one of the evasions the list openly accepts — a wrapper
/// function, a <c>search_path</c>, SQL built at runtime — every one of which needs prior write access on the
/// server. This needed a comment.
/// </para>
/// <para>
/// Reading tokens fixes three things at once and is the same call <see cref="SqlRedactor"/> and
/// <see cref="WriteGuard"/> already made: comments and whitespace are gone before the scan sees anything,
/// a delimited name is the same name as a bare one, and a <b>string literal is not a name</b> — the regex
/// refused <c>select 'pg_read_file('</c>, which mentions nothing and calls nothing.
/// </para>
/// </summary>
internal static class SqlNameScan
{
    /// <summary>
    /// Every identifier immediately followed by <c>(</c>, lower-cased with its delimiters removed. A
    /// qualified call yields its last part, so <c>pg_catalog.pg_read_file(…)</c> and
    /// <c>"pg_read_file"(…)</c> both come back as <c>pg_read_file</c> — the same function, which is the
    /// whole point of matching on the name rather than on the text.
    /// </summary>
    public static HashSet<string> CalledNames(IList<IToken> tokens)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;

        foreach (var token in Visible(tokens))
        {
            if (token.Text == "(" && previous is not null) names.Add(previous);
            previous = Bare(token.Text);
        }

        return names;
    }

    /// <summary>
    /// Every identifier the statement mentions, called or not — for the relations a read may not touch. A
    /// name in a string literal is not a mention, which falls out of reading tokens rather than text.
    /// </summary>
    public static HashSet<string> MentionedNames(IList<IToken> tokens)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Visible(tokens))
        {
            if (Bare(token.Text) is { Length: > 0 } name) names.Add(name);
        }

        return names;
    }

    /// <summary>Default-channel tokens only: ANTLR puts comments and whitespace on the hidden channel, which
    /// is exactly the difference between this and matching the text.</summary>
    private static IEnumerable<IToken> Visible(IList<IToken> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.Type == TokenConstants.EOF) continue;
            if (token.Channel != TokenConstants.DefaultChannel) continue;
            yield return token;
        }
    }

    /// <summary>
    /// A token's text as a bare name: delimiters stripped, so <c>"pg_authid"</c> and <c>[pg_authid]</c> are
    /// the name they delimit. A string literal keeps its quotes and therefore never equals a name, which is
    /// the behaviour wanted rather than an accident.
    /// </summary>
    private static string Bare(string text)
    {
        if (text.Length >= 2)
        {
            if (text[0] == '"' && text[^1] == '"') return text[1..^1].Replace("\"\"", "\"");
            if (text[0] == '[' && text[^1] == ']') return text[1..^1].Replace("]]", "]");
        }

        return text;
    }
}
