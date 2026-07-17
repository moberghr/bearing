using Antlr4.Runtime;
using Antlr4CodeCompletion.Core.CodeCompletion;
using Squirrel.Core.Completion;
using Squirrel.Core.Schema;

namespace Squirrel.Sql;

/// <summary>
/// Schema-aware SQL completion built on ANTLR + antlr4-c3. Pure and synchronous: given the SQL
/// text, a caret offset, and a schema snapshot, it returns ranked suggestions. No I/O, no UI —
/// the app layer runs it off the UI thread and feeds the result to the editor's completion window.
///
/// M1 scope: caret→token-index resolution, c3 candidate collection, intent classification via
/// <see cref="PgCompletionRules"/>, keyword suggestions, and flat (non-alias-aware) table/column
/// suggestions. Alias resolution and FK smart-joins arrive in later milestones.
/// </summary>
public sealed class CompletionEngine : ICompletionEngine
{
    public CompletionResult Complete(string sql, int caretOffset, ISchemaSnapshot schema)
    {
        caretOffset = Math.Clamp(caretOffset, 0, sql.Length);

        var parsed = PgParsing.Create(sql);
        parsed.Tokens.Fill();

        var caret = ResolveCaret(parsed.Tokens, caretOffset);

        // Prime the parser over the statement so c3 has a walkable token stream, then rewind.
        try { parsed.Parser.root(); } catch { /* partial SQL: recovery is expected */ }
        parsed.Parser.Reset();

        var core = new CodeCompletionCore(parsed.Parser, PgCompletionRules.PreferredRules.ToHashSet(),
            PgCompletionRules.IgnoredTokens.ToHashSet());
        var candidates = core.CollectCandidates(caret.TokenIndex, context: null);

        var suggestions = new List<Suggestion>();
        var intents = new HashSet<CompletionIntent>();

        foreach (var ruleId in candidates.Rules.Keys)
            intents.Add(PgCompletionRules.Classify(ruleId));

        if (intents.Contains(CompletionIntent.TablePosition))
            suggestions.AddRange(TableSuggestions(schema));

        if (intents.Contains(CompletionIntent.ColumnPosition))
            suggestions.AddRange(ColumnSuggestions(schema));

        // Keyword candidates (token types with a quoted literal name in the grammar).
        foreach (var tokenType in candidates.Tokens.Keys)
        {
            var kw = KeywordText(parsed.Parser.Vocabulary, tokenType);
            if (kw is not null)
                suggestions.Add(new Suggestion
                {
                    DisplayText = kw,
                    ReplacementText = kw,
                    Kind = SuggestionKind.Keyword,
                    Priority = 1,
                });
        }

        var ranked = suggestions
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CompletionResult(ranked, caret.ReplacementStart, caret.ReplacementLength);
    }

    /// <summary>The candidate intents at the caret (exposed for pinning tests).</summary>
    public IReadOnlySet<CompletionIntent> IntentsAt(string sql, int caretOffset)
    {
        caretOffset = Math.Clamp(caretOffset, 0, sql.Length);
        var parsed = PgParsing.Create(sql);
        parsed.Tokens.Fill();
        var caret = ResolveCaret(parsed.Tokens, caretOffset);
        try { parsed.Parser.root(); } catch { }
        parsed.Parser.Reset();

        var core = new CodeCompletionCore(parsed.Parser, PgCompletionRules.PreferredRules.ToHashSet(),
            PgCompletionRules.IgnoredTokens.ToHashSet());
        var candidates = core.CollectCandidates(caret.TokenIndex, context: null);

        var intents = new HashSet<CompletionIntent>();
        foreach (var ruleId in candidates.Rules.Keys)
            intents.Add(PgCompletionRules.Classify(ruleId));
        return intents;
    }

    private static IEnumerable<Suggestion> TableSuggestions(ISchemaSnapshot schema)
        => schema.Tables.Select(t => new Suggestion
        {
            DisplayText = t.Name,
            DetailText = t.Schema,
            ReplacementText = t.Name,
            Kind = t.Kind == PgRelKind.View ? SuggestionKind.View : SuggestionKind.Table,
            Priority = 10,
            Description = $"{t.Kind}: {t.Schema}.{t.Name}",
        });

    private static IEnumerable<Suggestion> ColumnSuggestions(ISchemaSnapshot schema)
        => schema.Tables
            .SelectMany(t => schema.ColumnsOf(t.Oid).Select(c => (t, c)))
            .Select(x => new Suggestion
            {
                DisplayText = x.c.Name,
                DetailText = $"{x.t.Name}",
                ReplacementText = x.c.Name,
                Kind = SuggestionKind.Column,
                Priority = 8,
                Description = $"{x.t.Schema}.{x.t.Name}.{x.c.Name} : {x.c.DataType}",
            });

    /// <summary>A displayable keyword for a token type, or null for non-keyword tokens.</summary>
    private static string? KeywordText(IVocabulary vocab, int tokenType)
    {
        var literal = vocab.GetLiteralName(tokenType);
        if (literal is null) return null;                 // identifiers, literals, operators-without-names
        var text = literal.Trim('\'');
        // Keep alphabetic keywords (SELECT, FROM, JOIN…); drop punctuation literals ('(' etc.).
        return text.Length > 0 && text.All(ch => char.IsLetter(ch) || ch == '_') ? text : null;
    }

    private readonly record struct CaretResolution(int TokenIndex, int ReplacementStart, int ReplacementLength);

    /// <summary>
    /// Map a character offset to the c3 caret token index + the source span a committed suggestion
    /// should overwrite. If the caret sits inside a word token, that token is being edited and gets
    /// replaced; otherwise the suggestion is inserted at the caret.
    /// </summary>
    private static CaretResolution ResolveCaret(BufferedTokenStream stream, int caret)
    {
        var tokens = stream.GetTokens();
        IToken? eof = tokens.Count > 0 ? tokens[^1] : null;

        foreach (var t in tokens)
        {
            if (t.Type == TokenConstants.EOF) break;

            var start = t.StartIndex;
            var endExclusive = t.StopIndex + 1;

            if (caret <= start)
                // Caret is before this token begins → insert here, complete at this token index.
                return new CaretResolution(t.TokenIndex, caret, 0);

            if (caret <= endExclusive)
            {
                if (IsWord(t))
                    // Editing this identifier/keyword: replace the whole token.
                    return new CaretResolution(t.TokenIndex, start, endExclusive - start);
                return new CaretResolution(t.TokenIndex, caret, 0);
            }
        }

        // Past the last real token → complete at EOF, inserting.
        return new CaretResolution(eof?.TokenIndex ?? 0, caret, 0);
    }

    private static bool IsWord(IToken t)
    {
        var s = t.Text;
        if (string.IsNullOrEmpty(s)) return false;
        return (char.IsLetter(s[0]) || s[0] == '_')
               && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$');
    }
}
