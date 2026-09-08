using System;
using System.Collections.Generic;
using System.Text;
using Antlr4.Runtime;
using Bearing.Core.Workspace;

namespace Bearing.Sql;

/// <summary>
/// Renders a <see cref="SqlFormatPlan"/> back to text by walking the token stream.
/// <para>
/// This is where the formatter's safety comes from, and it is worth being explicit about why: the writer
/// only ever emits <b>token text</b> and <b>whitespace between tokens</b>. It never synthesises a token,
/// never drops one, and never splits or joins two. So "layout only, never rewrite" is a property of the
/// design rather than of the care taken — a dollar-quoted body is one token and comes out byte for byte, and
/// <c>#&gt;&gt;</c>, <c>||</c> and <c>&lt;@</c> cannot be pulled apart the way a regex tokenizer pulls them
/// apart, because they were never characters here in the first place.
/// </para>
/// <para>
/// Comments survive for the same reason: the Postgres lexer puts them on the hidden channel, so they are in
/// the token stream and the writer sees them. Printing from the parse tree is what loses comments — they are
/// not nodes.
/// </para>
/// </summary>
internal static class SqlFormatWriter
{
    public static string Write(string sql, IList<IToken> tokens, SqlFormatPlan plan, SqlFormatOptions options)
    {
        // Match the file's own line endings. Emitting LF into a CRLF buffer would rewrite every line in the
        // statement, turning a formatting change into a whole-file diff for anyone on Windows.
        var newline = sql.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var output = new StringBuilder(sql.Length + 64);
        IToken? previous = null;

        foreach (var token in tokens)
        {
            if (token.Type == TokenConstants.EOF) break;
            // Whitespace is regenerated from the plan (and reproduced from the source for a Keep gap), so
            // the whitespace tokens themselves are never emitted.
            if (token.Type is PostgreSQLLexer.Whitespace or PostgreSQLLexer.Newline) continue;

            var gap = plan.GapBefore(token.TokenIndex);
            var indent = plan.IndentAt(token.TokenIndex);

            if (IsComment(token))
                (gap, indent) = CommentGap(sql, tokens, plan, token, previous);

            // A line comment runs to end of line, so whatever follows it must start on a new one or it is
            // swallowed into the comment. This is the one place the writer can create a bug that changes
            // meaning, so it is forced here rather than left to the layout rules to remember.
            if (previous is not null && previous.Type == PostgreSQLLexer.LineComment && gap is not Gap.Line and not Gap.Blank)
            {
                gap = Gap.Line;
                if (indent == 0) indent = plan.IndentAt(token.TokenIndex);
            }

            Emit(output, sql, gap, indent, previous, token, newline, options.IndentWidth);
            output.Append(Text(token, plan, options));
            previous = token;
        }

        // Whatever trailed the last token — usually the file's final newline — is the user's, not ours.
        if (previous is not null && previous.StopIndex + 1 < sql.Length)
            output.Append(sql, previous.StopIndex + 1, sql.Length - previous.StopIndex - 1);

        return output.ToString();
    }

    private static void Emit(
        StringBuilder output, string sql, Gap gap, int indent, IToken? previous, IToken token,
        string newline, int indentWidth)
    {
        // Nothing has been written yet: a leading break would just indent the file's first line.
        if (output.Length == 0)
        {
            if (gap == Gap.Keep && previous is null && token.StartIndex > 0)
                output.Append(sql, 0, token.StartIndex);
            return;
        }

        switch (gap)
        {
            case Gap.Keep:
                if (previous is not null)
                    output.Append(sql, previous.StopIndex + 1, token.StartIndex - previous.StopIndex - 1);
                break;

            case Gap.Auto:
                // A single space where the source had any separation, nothing where the two tokens were
                // already touching. That is what keeps a::text, count(*) and f(x) intact without a table of
                // spacing exceptions, and collapsing whitespace can never merge two tokens.
                if (previous is not null && token.StartIndex > previous.StopIndex + 1) output.Append(' ');
                break;

            case Gap.None:
                break;

            case Gap.Space:
                output.Append(' ');
                break;

            case Gap.Blank:
                output.Append(newline);
                goto case Gap.Line;

            case Gap.Line:
                output.Append(newline).Append(' ', indent * indentWidth);
                break;
        }
    }

    /// <summary>
    /// Where a comment goes. One that had a line to itself keeps one, at the indent of the code it sits
    /// above; one that trailed code stays trailing. Which it was is read from the line numbers rather than
    /// from the whitespace, so it holds regardless of how the source was indented.
    /// </summary>
    private static (Gap Gap, int Indent) CommentGap(
        string sql, IList<IToken> tokens, SqlFormatPlan plan, IToken comment, IToken? previous)
    {
        if (!plan.IsManaged(comment.TokenIndex)) return (Gap.Keep, 0);

        var ownLine = previous is null || comment.Line > previous.Line;
        if (!ownLine) return (Gap.Space, 0);

        // Indent it with the code it introduces, not the code it follows.
        for (var i = comment.TokenIndex + 1; i < tokens.Count; i++)
        {
            var next = tokens[i];
            if (next.Type is PostgreSQLLexer.Whitespace or PostgreSQLLexer.Newline || IsComment(next)) continue;
            if (next.Type == TokenConstants.EOF) break;
            return (Gap.Line, plan.IndentAt(i));
        }
        return (Gap.Line, 0);
    }

    /// <summary>
    /// A token's text, with keyword case applied. Only a token the layout pass claimed, that the grammar did
    /// not reach through an identifier position, and that is in the curated keyword set is ever recased.
    /// </summary>
    private static string Text(IToken token, SqlFormatPlan plan, SqlFormatOptions options)
    {
        if (options.KeywordCase == SqlKeywordCase.Preserve) return token.Text;
        if (!plan.IsManaged(token.TokenIndex)) return token.Text;
        if (plan.KeepsCase(token.TokenIndex)) return token.Text;
        if (!SqlFormatKeywords.Uppercased.Contains(token.Type)) return token.Text;

        return options.KeywordCase == SqlKeywordCase.Lower
            ? token.Text.ToLowerInvariant()
            : token.Text.ToUpperInvariant();
    }

    private static bool IsComment(IToken token)
        => token.Type is PostgreSQLLexer.LineComment or PostgreSQLLexer.BlockComment;
}
