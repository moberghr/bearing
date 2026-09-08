using System.Collections.Generic;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// The outcome of formatting. <see cref="Text"/> is always safe to put back in the editor: on a refusal it
/// is the input, unchanged.
/// </summary>
/// <param name="Text">The formatted SQL, or the original when nothing changed or the formatter refused.</param>
/// <param name="Changed">Whether <see cref="Text"/> differs from the input.</param>
/// <param name="Refusal">Why the formatter declined, or null when it did not. A refusal is a normal
/// outcome, not an error — partial SQL is what a query editor is full of.</param>
public sealed record SqlFormatResult(string Text, bool Changed, string? Refusal)
{
    public bool Refused => Refusal is not null;
}

/// <summary>
/// A Postgres SQL formatter: layout only, never a rewrite.
/// <para>
/// Pure — <c>string</c> in, <c>string</c> out, no clock, no I/O, no UI — so the behaviour can be pinned by
/// ordinary unit tests rather than by driving an editor (§2.5).
/// </para>
/// <para><b>What it will not do.</b> It never reorders, adds or removes a token, and never changes a
/// literal or an identifier. It changes whitespace, and it uppercases a curated set of keywords
/// (<see cref="SqlFormatKeywords"/>), which Postgres treats as insignificant. Three things enforce that
/// rather than merely intending it:
/// <list type="number">
/// <item>the writer emits token text and whitespace only, so operators like <c>#&gt;&gt;</c> and bodies like
/// <c>$$ … $$</c> are single tokens that cannot be split or edited;</item>
/// <item>a statement whose shape has no layout rules keeps its original whitespace byte for byte, so
/// "unrecognised" means "untouched" rather than "flattened";</item>
/// <item>every result is checked by <see cref="SqlFormatGuard"/> — the output is re-lexed and compared token
/// by token against the input, and any difference beyond whitespace and keyword case makes the formatter
/// hand back the input and say why.</item>
/// </list>
/// </para>
/// <para>
/// SQL that does not parse is not formatted at all. Guessing at the layout of something we could not read is
/// how a formatter comes to rewrite a statement that then runs against production.
/// </para>
/// </summary>
public static class SqlFormat
{
    /// <summary>Format a whole SQL text — one statement or a batch.</summary>
    /// <param name="options">Layout and case dials; <see cref="SqlFormatOptions.Default"/> when omitted.</param>
    public static SqlFormatResult Format(string sql, SqlFormatOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) return new SqlFormatResult(sql, false, null);

        var settings = options ?? SqlFormatOptions.Default;

        // The parse and the tree walk both recurse per nesting level, so they run on a stack sized for it.
        // Synchronous and joined, so this stays an ordinary pure function from the caller's side.
        return PgParsing.OnDeepStack(() => FormatCore(sql, settings));
    }

    private static SqlFormatResult FormatCore(string sql, SqlFormatOptions options)
    {
        var parsed = PgParsing.Create(sql);
        parsed.Tokens.Fill();
        var tokens = parsed.Tokens.GetTokens();

        // Checked before parsing, never after. Even on the deep stack there is a depth past which the
        // parser overflows, and a StackOverflowException cannot be caught in .NET — the process simply
        // dies. See PgParsing.MaxNestingDepth: this is a crash backstop, not a size limit.
        if (PgParsing.TooDeeplyNested(tokens))
            return new SqlFormatResult(sql, false,
                $"it nests more than {PgParsing.MaxNestingDepth} levels deep");

        var errors = new SyntaxErrorCollector();
        parsed.Parser.AddErrorListener(errors);

        PostgreSQLParser.RootContext root;
        try { root = parsed.Parser.root(); }
        catch { return new SqlFormatResult(sql, false, "the statement could not be parsed"); }

        if (errors.First is { } problem)
            return new SqlFormatResult(sql, false, $"the statement could not be parsed ({problem})");

        var plan = SqlFormatLayout.Build(root, tokens);
        var formatted = SqlFormatWriter.Write(sql, tokens, plan, options);

        if (SqlFormatGuard.Difference(sql, formatted, options.KeywordCase) is { } difference)
            return new SqlFormatResult(sql, false, $"the result would not have been the same SQL ({difference})");

        return new SqlFormatResult(formatted, !string.Equals(formatted, sql, System.StringComparison.Ordinal), null);
    }

    /// <summary>Records the first syntax error and counts the rest. The message is for a status bar, so the
    /// position matters more than the parser's own phrasing.</summary>
    private sealed class SyntaxErrorCollector : BaseErrorListener
    {
        public string? First { get; private set; }

        public override void SyntaxError(
            System.IO.TextWriter output, IRecognizer recognizer, IToken offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e)
            => First ??= $"line {line}, column {charPositionInLine + 1}";
    }
}
