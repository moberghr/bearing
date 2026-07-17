using System.Text.RegularExpressions;
using Antlr4.Runtime.Tree;
using Antlr4CodeCompletion.Core.CodeCompletion;
using Squirrel.Core.Completion;
using Squirrel.Core.Schema;

namespace Squirrel.Sql;

/// <summary>
/// Schema-aware SQL completion built on ANTLR + antlr4-c3. Pure and synchronous: given the SQL
/// text, caret offset, and a schema snapshot, returns ranked suggestions and the span they replace.
/// </summary>
public sealed partial class CompletionEngine : ICompletionEngine
{
    public CompletionResult Complete(string sql, int caretOffset, ISchemaSnapshot schema)
    {
        caretOffset = Math.Clamp(caretOffset, 0, sql.Length);

        var parsed = PgParsing.Create(sql);
        parsed.Tokens.Fill();
        var caret = ResolveCaret(parsed.Tokens, caretOffset);

        // Prime the parser over the statement so c3 has a walkable token stream, then rewind.
        try { parsed.Parser.root(); } catch { /* partial SQL */ }
        parsed.Parser.Reset();

        // FROM/JOIN scope is extracted resiliently (token-isolated), independent of select-list garbage.
        var sources = FromClauseExtractor.Extract(sql, schema);

        var core = new CodeCompletionCore(parsed.Parser, PgCompletionRules.PreferredRules.ToHashSet(),
            PgCompletionRules.IgnoredTokens.ToHashSet());
        var candidates = core.CollectCandidates(caret.TokenIndex, context: null);
        var intents = candidates.Rules.Keys.Select(PgCompletionRules.Classify).ToHashSet();

        var suggestions = new List<Suggestion>();
        var qualifier = AliasQualifierBefore(sql, caretOffset);

        if (qualifier is not null)
        {
            // After "alias." only that alias's columns (and FK-equality predicates joining it to an
            // in-scope table) make sense — never tables/joins/keywords, regardless of how c3 classifies
            // the caret in the surrounding (often broken) SQL.
            suggestions.AddRange(FkPredicateSuggestions(schema, sources, qualifier));
            suggestions.AddRange(ColumnSuggestions(schema, sources, qualifier));
        }
        else
        {
            if (intents.Contains(CompletionIntent.TablePosition))
            {
                suggestions.AddRange(TableSuggestions(schema, sources));
                if (sources.Count > 0)
                    suggestions.AddRange(JoinSuggestions(schema, sources));
            }

            if (intents.Contains(CompletionIntent.ColumnPosition))
                suggestions.AddRange(ColumnSuggestions(schema, sources, qualifier: null));

            foreach (var tokenType in candidates.Tokens.Keys)
            {
                var kw = KeywordText(parsed.Parser.Vocabulary, tokenType);
                if (kw is not null)
                    suggestions.Add(new Suggestion
                    {
                        DisplayText = kw, ReplacementText = kw, Kind = SuggestionKind.Keyword, Priority = 1,
                    });
            }
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
        return candidates.Rules.Keys.Select(PgCompletionRules.Classify).ToHashSet();
    }

    // ---- Table suggestions (auto-aliased, like the prototype) --------------------------------

    private static IEnumerable<Suggestion> TableSuggestions(ISchemaSnapshot schema, IReadOnlyList<TableRef> sources)
    {
        var existing = ExistingAliases(sources);
        foreach (var t in schema.Tables)
        {
            var alias = AliasResolver.Determine(t, existing);
            yield return new Suggestion
            {
                DisplayText = t.Name,
                FilterText = t.Name,
                DetailText = t.Schema,
                ReplacementText = $"{t.Name} {alias}",
                Kind = t.Kind == PgRelKind.View ? SuggestionKind.View : SuggestionKind.Table,
                Priority = 10,
                Description = $"{t.Kind}: {t.Schema}.{t.Name}",
            };
        }
    }

    // ---- FK-driven smart joins (bidirectional) -----------------------------------------------

    private static IEnumerable<Suggestion> JoinSuggestions(ISchemaSnapshot schema, IReadOnlyList<TableRef> sources)
    {
        var existing = ExistingAliases(sources);

        foreach (var src in sources)
        {
            if (src.Resolved is null) continue;
            var srcOid = src.Resolved.Oid;
            var srcAlias = src.EffectiveName;

            foreach (var fk in schema.ForeignKeysTouching(srcOid))
            {
                uint otherOid;
                IReadOnlyList<short> srcCols, otherCols;

                if (fk.ParentOid == srcOid)
                {
                    otherOid = fk.ReferencedOid; srcCols = fk.ParentAttNums; otherCols = fk.ReferencedAttNums;
                }
                else if (fk.ReferencedOid == srcOid)
                {
                    otherOid = fk.ParentOid; srcCols = fk.ReferencedAttNums; otherCols = fk.ParentAttNums;
                }
                else continue;

                var other = schema.Tables.FirstOrDefault(t => t.Oid == otherOid);
                if (other is null) continue;

                var alias = AliasResolver.Determine(other, existing);
                var preds = new List<string>();
                for (var i = 0; i < Math.Min(srcCols.Count, otherCols.Count); i++)
                    preds.Add($"{alias}.{ColumnName(schema, otherOid, otherCols[i])} = {srcAlias}.{ColumnName(schema, srcOid, srcCols[i])}");
                var predicate = string.Join(" and ", preds);

                yield return new Suggestion
                {
                    DisplayText = other.Name,
                    FilterText = other.Name,
                    DetailText = $"join → {srcAlias}",
                    TrailingText = predicate,
                    ReplacementText = $"{other.Name} {alias} on {predicate}",
                    Kind = SuggestionKind.Join,
                    Priority = 20,
                    Description = $"FK {fk.Name}: {other.Schema}.{other.Name} ⋈ {srcAlias}",
                };
            }
        }
    }

    // ---- FK-equality predicates after "alias." (e.g. in a WHERE) -----------------------------

    /// <summary>
    /// When the caret is at <c>alias.</c> and that alias's table has a foreign key to (or from)
    /// another in-scope source, offer the join equality — e.g. <c>country_id = c.country_id</c> —
    /// so a correlated WHERE/ON predicate completes in one keystroke.
    /// </summary>
    private static IEnumerable<Suggestion> FkPredicateSuggestions(
        ISchemaSnapshot schema, IReadOnlyList<TableRef> sources, string qualifier)
    {
        var owner = sources.FirstOrDefault(s =>
            string.Equals(s.EffectiveName, qualifier, StringComparison.OrdinalIgnoreCase));
        if (owner?.Resolved is null) yield break;
        var ownerOid = owner.Resolved.Oid;

        foreach (var other in sources)
        {
            if (other.Resolved is null || ReferenceEquals(other, owner)) continue;
            if (string.Equals(other.EffectiveName, qualifier, StringComparison.OrdinalIgnoreCase)) continue;
            var otherOid = other.Resolved.Oid;

            foreach (var fk in schema.ForeignKeysTouching(ownerOid))
            {
                IReadOnlyList<short> ownerCols, otherCols;
                if (fk.ParentOid == ownerOid && fk.ReferencedOid == otherOid)
                    (ownerCols, otherCols) = (fk.ParentAttNums, fk.ReferencedAttNums);
                else if (fk.ReferencedOid == ownerOid && fk.ParentOid == otherOid)
                    (ownerCols, otherCols) = (fk.ReferencedAttNums, fk.ParentAttNums);
                else continue;

                var preds = new List<string>();
                for (var i = 0; i < Math.Min(ownerCols.Count, otherCols.Count); i++)
                    preds.Add($"{ColumnName(schema, ownerOid, ownerCols[i])} = {other.EffectiveName}.{ColumnName(schema, otherOid, otherCols[i])}");
                if (preds.Count == 0) continue;
                var predicate = string.Join(" and ", preds);

                yield return new Suggestion
                {
                    DisplayText = predicate,
                    FilterText = ColumnName(schema, ownerOid, ownerCols[0]),
                    DetailText = $"fk → {other.EffectiveName}",
                    ReplacementText = predicate,
                    Kind = SuggestionKind.Join,
                    Priority = 30,
                    Description = $"FK {fk.Name}: {owner.EffectiveName} ⋈ {other.EffectiveName}",
                };
            }
        }
    }

    // ---- Column suggestions (alias-aware) ----------------------------------------------------

    private static IEnumerable<Suggestion> ColumnSuggestions(
        ISchemaSnapshot schema, IReadOnlyList<TableRef> sources, string? qualifier)
    {
        if (qualifier is not null)
        {
            var owner = sources.FirstOrDefault(s =>
                string.Equals(s.EffectiveName, qualifier, StringComparison.OrdinalIgnoreCase));
            if (owner?.Resolved is null) yield break;
            foreach (var c in schema.ColumnsOf(owner.Resolved.Oid))
                yield return ColumnSuggestion(c, owner.EffectiveName, qualified: false);
            yield break;
        }

        var resolved = sources.Where(s => s.Resolved is not null).ToList();
        if (resolved.Count > 0)
        {
            foreach (var src in resolved)
                foreach (var c in schema.ColumnsOf(src.Resolved!.Oid))
                    yield return ColumnSuggestion(c, src.EffectiveName, qualified: false);
            yield break;
        }

        // No FROM yet: offer every column (M3 fallback).
        foreach (var t in schema.Tables)
            foreach (var c in schema.ColumnsOf(t.Oid))
                yield return ColumnSuggestion(c, t.Name, qualified: false);
    }

    private static Suggestion ColumnSuggestion(PgColumn c, string owner, bool qualified) => new()
    {
        DisplayText = c.Name,
        FilterText = c.Name,
        DetailText = owner,
        ReplacementText = qualified ? $"{owner}.{c.Name}" : c.Name,
        Kind = SuggestionKind.Column,
        Priority = c.IsPrimaryKey ? 9 : 8,
        Description = $"{owner}.{c.Name} : {c.DataType}",
    };

    private static string ColumnName(ISchemaSnapshot schema, uint tableOid, short attNum)
        => schema.ColumnsOf(tableOid).FirstOrDefault(c => c.AttNum == attNum)?.Name ?? $"col{attNum}";

    private static List<string> ExistingAliases(IReadOnlyList<TableRef> sources)
        => sources.Select(s => s.EffectiveName).Where(a => !string.IsNullOrEmpty(a)).ToList();

    // ---- Caret + token helpers ---------------------------------------------------------------

    /// <summary>If the caret sits just after "&lt;identifier&gt;.", returns that identifier (the alias).</summary>
    private static string? AliasQualifierBefore(string sql, int caret)
    {
        var prefix = sql[..caret];
        var m = AliasDotRegex().Match(prefix);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_$]*)\s*\.\s*[A-Za-z0-9_$]*$")]
    private static partial Regex AliasDotRegex();

    private static string? KeywordText(Antlr4.Runtime.IVocabulary vocab, int tokenType)
    {
        var literal = vocab.GetLiteralName(tokenType);
        if (literal is null) return null;
        var text = literal.Trim('\'');
        return text.Length > 0 && text.All(ch => char.IsLetter(ch) || ch == '_') ? text : null;
    }

    private readonly record struct CaretResolution(int TokenIndex, int ReplacementStart, int ReplacementLength);

    private static CaretResolution ResolveCaret(Antlr4.Runtime.BufferedTokenStream stream, int caret)
    {
        var tokens = stream.GetTokens();
        var eof = tokens.Count > 0 ? tokens[^1] : null;

        foreach (var t in tokens)
        {
            if (t.Type == Antlr4.Runtime.TokenConstants.EOF) break;

            var start = t.StartIndex;
            var endExclusive = t.StopIndex + 1;

            if (caret <= start)
                return new CaretResolution(t.TokenIndex, caret, 0);

            if (caret <= endExclusive)
            {
                if (IsWord(t))
                    return new CaretResolution(t.TokenIndex, start, endExclusive - start);
                return new CaretResolution(t.TokenIndex, caret, 0);
            }
        }

        return new CaretResolution(eof?.TokenIndex ?? 0, caret, 0);
    }

    private static bool IsWord(Antlr4.Runtime.IToken t)
    {
        var s = t.Text;
        if (string.IsNullOrEmpty(s)) return false;
        return (char.IsLetter(s[0]) || s[0] == '_')
               && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$');
    }
}
