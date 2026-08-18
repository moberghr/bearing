using System.Text.RegularExpressions;
using Antlr4.Runtime.Tree;
using Antlr4CodeCompletion.Core.CodeCompletion;
using Bearing.Core.Completion;
using Bearing.Core.Schema;

namespace Bearing.Sql;

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
        var aliasSlot = CaretIsInAliasSlot(schema, parsed.Tokens.GetTokens(), caret.TokenIndex);

        // Prime the parser over the statement so c3 has a walkable token stream, then rewind.
        try { parsed.Parser.root(); } catch { /* partial SQL */ }
        parsed.Parser.Reset();

        // FROM/JOIN scope is extracted resiliently (token-isolated), independent of select-list garbage.
        var sources = FromClauseExtractor.Extract(sql, schema);

        var core = new CodeCompletionCore(parsed.Parser, PgCompletionRules.PreferredRules.ToHashSet(),
            PgCompletionRules.IgnoredTokens.ToHashSet());
        var candidates = core.CollectCandidates(caret.TokenIndex, context: null);
        var intents = candidates.Rules.Keys.Select(PgCompletionRules.Classify).ToHashSet();

        // A half-typed alias is a name the user is inventing — nothing in the catalog or the grammar
        // belongs there, and whatever sat under Enter would overwrite it. An *empty* alias slot still
        // gets keywords (as / join / where), which is what actually follows a named source.
        if (aliasSlot && caret.ReplacementLength > 0)
            return new CompletionResult(Array.Empty<Suggestion>(), caret.ReplacementStart, caret.ReplacementLength);

        var suggestions = new List<Suggestion>();
        var qualifier = AliasQualifierBefore(sql, caretOffset);

        if (qualifier is not null && IsResolvedSource(sources, qualifier))
        {
            // After "alias." only that alias's columns (and, where a predicate can actually begin, the
            // FK equality joining it to an in-scope table) make sense — never tables/joins/keywords,
            // regardless of how c3 classifies the caret in the surrounding (often broken) SQL.
            if (FkPredicateFits(parsed.Tokens.GetTokens(), caret.TokenIndex))
                suggestions.AddRange(FkPredicateSuggestions(schema, sources, qualifier));
            suggestions.AddRange(ColumnSuggestions(schema, sources, qualifier));
        }
        else if (qualifier is not null && SchemaNamed(schema, qualifier) is { } qualifierSchema)
        {
            // "public." is a schema qualifier, not an alias — an in-scope alias of the same name wins
            // (checked above), otherwise the only sensible answer is that schema's relations. This
            // branch used to fall through to the column path and produce an empty popup.
            suggestions.AddRange(TableSuggestions(schema, sources, onlySchema: qualifierSchema));
        }
        else if (qualifier is null)
        {
            // An alias slot is still a "table position" to c3 (an alias is just an identifier), but the
            // one thing that cannot belong there is another relation — offering them made accepting one
            // overwrite the alias: `select * from film f` → `select * from film film f2`. Keywords still
            // come through, which is what actually follows a named source (as / join / where).
            if (intents.Contains(CompletionIntent.TablePosition) && !aliasSlot)
            {
                suggestions.AddRange(TableSuggestions(schema, sources));
                suggestions.AddRange(SchemaSuggestions(schema));
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
                        DisplayText = kw,
                        ReplacementText = kw,
                        Kind = SuggestionKind.Keyword,
                        Priority = 1,
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

    /// <param name="onlySchema">When set, only that schema's relations, and inserted bare — the caret
    /// already sits after <c>schema.</c>.</param>
    private static IEnumerable<Suggestion> TableSuggestions(
        ISchemaSnapshot schema, IReadOnlyList<TableRef> sources, string? onlySchema = null)
    {
        var existing = ExistingAliases(sources);
        foreach (var t in schema.Tables)
        {
            if (onlySchema is not null
                && !string.Equals(t.Schema, onlySchema, StringComparison.OrdinalIgnoreCase)) continue;

            var alias = AliasResolver.Determine(t, existing);
            // A relation outside search_path (or shadowed by a same-named one earlier in it) does not
            // resolve bare, so the insertion carries its schema even though the label doesn't.
            var name = onlySchema is null && !ResolvesUnqualified(schema, t)
                ? $"{Q(t.Schema)}.{Q(t.Name)}"
                : Q(t.Name);
            yield return new Suggestion
            {
                DisplayText = t.Name,
                FilterText = t.Name,
                DetailText = t.Schema,
                ReplacementText = $"{name} {alias}",
                Kind = t.Kind == RelationKind.View ? SuggestionKind.View : SuggestionKind.Table,
                Priority = 10,
                Description = $"{t.Kind}: {t.Schema}.{t.Name}",
            };
        }
    }

    /// <summary>
    /// Schema names at a table position, so <c>audit.</c> is reachable without knowing what's in it.
    /// Ranked below relations: the common case is picking a table, not narrowing to a schema first.
    /// </summary>
    private static IEnumerable<Suggestion> SchemaSuggestions(ISchemaSnapshot schema)
    {
        var reachable = new HashSet<string>(schema.SearchPath, StringComparer.OrdinalIgnoreCase);
        foreach (var name in schema.Schemas)
            yield return new Suggestion
            {
                DisplayText = name,
                FilterText = name,
                DetailText = "schema",
                // The trailing dot leaves the caret where the relation list continues.
                ReplacementText = $"{Q(name)}.",
                Kind = SuggestionKind.Schema,
                Priority = 5,
                Description = reachable.Contains(name)
                    ? $"schema {name} (in search_path)"
                    : $"schema {name}",
            };
    }

    /// <summary>True when writing <paramref name="table"/>'s bare name resolves to that same relation:
    /// its schema is on the search_path and nothing earlier there shadows the name.</summary>
    private static bool ResolvesUnqualified(ISchemaSnapshot schema, TableInfo table)
        => schema.SearchPath.Contains(table.Schema, StringComparer.OrdinalIgnoreCase)
           && schema.ResolveTable(null, table.Name)?.Id == table.Id;

    /// <summary>
    /// True when <paramref name="qualifier"/> names a FROM/JOIN source that resolved to a real relation.
    /// Resolution matters: <c>from audit.</c> leaves a half-typed source named <c>audit</c> behind, and
    /// treating that as an alias is what made the schema qualifier answer with an empty popup.
    /// </summary>
    /// <summary>
    /// True when a whole predicate (<c>film_id = fa.film_id</c>) can start at the caret, as opposed to
    /// just a column reference. The offer is only sensible directly after a boolean context opens —
    /// <c>on</c>, <c>where</c>, <c>and</c>/<c>or</c>/<c>not</c>, <c>having</c>, an opening paren. On the
    /// right-hand side of an existing comparison (<c>on fa.film_id = f.|</c>) it produced
    /// <c>on fa.film_id = f.film_id = fa.film_id</c>, and in a select list it is noise.
    /// </summary>
    private static bool FkPredicateFits(IList<Antlr4.Runtime.IToken> toks, int caretTokenIndex)
    {
        // Walk back to the qualifier: the caret may sit on the column being typed, on the dot, or between.
        var i = PrevMeaningful(toks, caretTokenIndex);
        if (i >= 0 && IsNameToken(toks[i])) i = PrevMeaningful(toks, i - 1);
        if (i < 0 || toks[i].Type != PostgreSQLParser.DOT) return false;

        i = PrevMeaningful(toks, i - 1);
        if (i < 0 || !IsNameToken(toks[i])) return false;

        var before = PrevMeaningful(toks, i - 1);
        return before >= 0 && toks[before].Type is PostgreSQLParser.ON or PostgreSQLParser.WHERE
            or PostgreSQLParser.AND or PostgreSQLParser.OR or PostgreSQLParser.NOT
            or PostgreSQLParser.HAVING or PostgreSQLParser.OPEN_PAREN;
    }

    /// <summary>
    /// True when the caret sits where a source's alias goes, the relation before it already being named:
    /// <c>from film f|</c>, <c>from film |</c>, <c>from film as f|</c>, <c>from public.film f|</c>. Keyed
    /// off the token before the caret rather than the parse tree, so it survives half-typed SQL like the
    /// rest of this file.
    /// </summary>
    private static bool CaretIsInAliasSlot(ISchemaSnapshot schema, IList<Antlr4.Runtime.IToken> toks, int caretTokenIndex)
    {
        var i = PrevMeaningful(toks, caretTokenIndex - 1);
        if (i < 0) return false;

        if (toks[i].Type == PostgreSQLParser.AS)
        {
            i = PrevMeaningful(toks, i - 1);
            if (i < 0) return false;
        }
        if (!IsNameToken(toks[i])) return false;

        // Walk back over a qualified name (schema.relation), collecting its parts.
        var parts = new List<string> { toks[i].Text };
        var before = PrevMeaningful(toks, i - 1);
        while (before >= 0 && toks[before].Type == PostgreSQLParser.DOT)
        {
            var name = PrevMeaningful(toks, before - 1);
            if (name < 0 || !IsNameToken(toks[name])) break;
            parts.Insert(0, toks[name].Text);
            before = PrevMeaningful(toks, name - 1);
        }

        // Only a FROM/JOIN list puts a relation name there; anything else (a select-list expression, a
        // WHERE) is not an alias slot even though it may end in an identifier.
        if (before < 0 || toks[before].Type is not (PostgreSQLParser.FROM or PostgreSQLParser.JOIN
            or PostgreSQLParser.COMMA or PostgreSQLParser.LATERAL_P))
            return false;

        var relation = PgIdentifier.Unquote(parts[^1]);
        var schemaName = parts.Count >= 2 ? PgIdentifier.Unquote(parts[^2]) : null;
        return schema.ResolveTable(schemaName, relation) is not null;
    }

    /// <summary>Index of the nearest token at or before <paramref name="from"/> that carries meaning
    /// (skipping whitespace/comments on the hidden channel, and EOF), or -1.</summary>
    private static int PrevMeaningful(IList<Antlr4.Runtime.IToken> toks, int from)
    {
        for (var i = Math.Min(from, toks.Count - 1); i >= 0; i--)
            if (toks[i].Channel == Antlr4.Runtime.TokenConstants.DefaultChannel
                && toks[i].Type != Antlr4.Runtime.TokenConstants.EOF)
                return i;
        return -1;
    }

    private static bool IsNameToken(Antlr4.Runtime.IToken t)
        => t.Type is PostgreSQLParser.Identifier or PostgreSQLParser.QuotedIdentifier;

    private static bool IsResolvedSource(IReadOnlyList<TableRef> sources, string qualifier)
        => sources.Any(s => s.Resolved is not null
                            && string.Equals(s.EffectiveName, qualifier, StringComparison.OrdinalIgnoreCase));

    /// <summary>The schema named <paramref name="qualifier"/> as the catalog spells it, or null.</summary>
    private static string? SchemaNamed(ISchemaSnapshot schema, string qualifier)
        => schema.Schemas.FirstOrDefault(s => string.Equals(s, qualifier, StringComparison.OrdinalIgnoreCase));

    // ---- FK-driven smart joins (bidirectional) -----------------------------------------------

    private static IEnumerable<Suggestion> JoinSuggestions(ISchemaSnapshot schema, IReadOnlyList<TableRef> sources)
    {
        var existing = ExistingAliases(sources);

        foreach (var src in sources)
        {
            if (src.Resolved is null) continue;
            var srcOid = src.Resolved.Id;
            var srcAlias = src.EffectiveRef;

            foreach (var fk in schema.ForeignKeysTouching(srcOid))
            {
                long otherOid;
                IReadOnlyList<int> srcCols, otherCols;

                if (fk.ParentTableId == srcOid)
                {
                    otherOid = fk.ReferencedTableId; srcCols = fk.ParentOrdinals; otherCols = fk.ReferencedOrdinals;
                }
                else if (fk.ReferencedTableId == srcOid)
                {
                    otherOid = fk.ParentTableId; srcCols = fk.ReferencedOrdinals; otherCols = fk.ParentOrdinals;
                }
                else continue;

                var other = schema.Tables.FirstOrDefault(t => t.Id == otherOid);
                if (other is null) continue;

                var alias = AliasResolver.Determine(other, existing);
                var preds = new List<string>();
                for (var i = 0; i < Math.Min(srcCols.Count, otherCols.Count); i++)
                    preds.Add($"{alias}.{Q(ColumnName(schema, otherOid, otherCols[i]))} = {srcAlias}.{Q(ColumnName(schema, srcOid, srcCols[i]))}");
                var predicate = string.Join(" and ", preds);

                yield return new Suggestion
                {
                    DisplayText = other.Name,
                    FilterText = other.Name,
                    DetailText = $"join → {SourceLabel(src)}",
                    TrailingText = predicate,
                    ReplacementText = $"{Q(other.Name)} {alias} on {predicate}",
                    Kind = SuggestionKind.Join,
                    Priority = 20,
                    Description = $"FK {fk.Name}: {other.Schema}.{other.Name} ⋈ {SourceLabel(src)}",
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
        var ownerOid = owner.Resolved.Id;

        foreach (var other in sources)
        {
            if (other.Resolved is null || ReferenceEquals(other, owner)) continue;
            if (string.Equals(other.EffectiveName, qualifier, StringComparison.OrdinalIgnoreCase)) continue;
            var otherOid = other.Resolved.Id;

            foreach (var fk in schema.ForeignKeysTouching(ownerOid))
            {
                IReadOnlyList<int> ownerCols, otherCols;
                if (fk.ParentTableId == ownerOid && fk.ReferencedTableId == otherOid)
                    (ownerCols, otherCols) = (fk.ParentOrdinals, fk.ReferencedOrdinals);
                else if (fk.ReferencedTableId == ownerOid && fk.ParentTableId == otherOid)
                    (ownerCols, otherCols) = (fk.ReferencedOrdinals, fk.ParentOrdinals);
                else continue;

                var preds = new List<string>();
                for (var i = 0; i < Math.Min(ownerCols.Count, otherCols.Count); i++)
                    preds.Add($"{Q(ColumnName(schema, ownerOid, ownerCols[i]))} = {other.EffectiveRef}.{Q(ColumnName(schema, otherOid, otherCols[i]))}");
                if (preds.Count == 0) continue;
                var predicate = string.Join(" and ", preds);

                yield return new Suggestion
                {
                    DisplayText = predicate,
                    FilterText = ColumnName(schema, ownerOid, ownerCols[0]),
                    DetailText = $"fk → {SourceLabel(other)}",
                    ReplacementText = predicate,
                    Kind = SuggestionKind.Join,
                    Priority = 30,
                    Description = $"FK {fk.Name}: {SourceLabel(owner)} ⋈ {SourceLabel(other)}",
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
            foreach (var c in schema.ColumnsOf(owner.Resolved.Id))
                yield return ColumnSuggestion(c, owner.EffectiveName);
            yield break;
        }

        var resolved = sources.Where(s => s.Resolved is not null).ToList();
        if (resolved.Count > 0)
        {
            foreach (var src in resolved)
                foreach (var c in schema.ColumnsOf(src.Resolved!.Id))
                    // An aliased source qualifies its columns on insertion: in a select list, ORDER BY or
                    // WHERE, a bare `id` is ambiguous the moment a second source joins in, and the alias
                    // is what the rest of the statement refers to it by.
                    yield return ColumnSuggestion(c, src.EffectiveName,
                        qualifier: src.Alias is not null ? src.EffectiveRef : null);
            yield break;
        }

        // No FROM yet: offer every column (M3 fallback).
        foreach (var t in schema.Tables)
            foreach (var c in schema.ColumnsOf(t.Id))
                yield return ColumnSuggestion(c, t.Name);
    }

    /// <param name="qualifier">Prefix for the inserted text (<c>f.id</c>); null inserts the bare column,
    /// which is what the <c>alias.</c> path needs — the qualifier is already typed there.</param>
    private static Suggestion ColumnSuggestion(ColumnInfo c, string owner, string? qualifier = null) => new()
    {
        DisplayText = c.Name,
        FilterText = c.Name,
        DetailText = owner,
        ReplacementText = qualifier is null ? Q(c.Name) : $"{qualifier}.{Q(c.Name)}",
        Kind = SuggestionKind.Column,
        Priority = c.IsPrimaryKey ? 9 : 8,
        Description = $"{owner}.{c.Name} : {c.DataType}",
    };

    /// <summary>Quote a catalog name for insertion when the bare form wouldn't round-trip
    /// (<c>__MigrationHistory</c>, <c>order</c>); ordinary lower-case names stay bare so the
    /// completed SQL reads the way it would if it were typed by hand.</summary>
    private static string Q(string identifier) => PgIdentifier.QuoteIfNeeded(identifier);

    /// <summary>
    /// How a hint names a source: the relation plus the alias the query gave it (<c>film f</c>). The alias
    /// on its own — which is all <c>join → f</c> showed — doesn't say what you are joining to, and a query
    /// with several single-letter aliases makes it a guessing game.
    /// </summary>
    private static string SourceLabel(TableRef src)
    {
        var name = src.Resolved?.Name ?? src.RawName;
        return src.Alias is { Length: > 0 } alias ? $"{name} {alias}" : name;
    }

    private static string ColumnName(ISchemaSnapshot schema, long tableId, int ordinal)
        => schema.ColumnsOf(tableId).FirstOrDefault(c => c.Ordinal == ordinal)?.Name ?? $"col{ordinal}";

    private static List<string> ExistingAliases(IReadOnlyList<TableRef> sources)
        => sources.Select(s => s.EffectiveName).Where(a => !string.IsNullOrEmpty(a)).ToList();

    // ---- Caret + token helpers ---------------------------------------------------------------

    /// <summary>
    /// If the caret sits just after "&lt;identifier&gt;.", returns that identifier (the alias),
    /// unquoted — a quoted qualifier (<c>"__MigrationHistory".</c>) is the same source as the bare
    /// one, and every match downstream is against unquoted names.
    /// </summary>
    private static string? AliasQualifierBefore(string sql, int caret)
    {
        var prefix = sql[..caret];
        var m = AliasDotRegex().Match(prefix);
        return m.Success ? PgIdentifier.Unquote(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"(""(?:[^""]|"""")*""|[A-Za-z_][A-Za-z0-9_$]*)\s*\.\s*(?:""(?:[^""]|"""")*|[A-Za-z0-9_$]*)$")]
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

    /// <summary>
    /// True when the token under the caret is a partially-typed name the completion should overwrite.
    /// A quoted identifier counts — including the unterminated <c>"__Mig</c> the user is mid-way
    /// through typing, which the lexer still hands back as one token starting with a quote. Without
    /// that, accepting an item appended instead of replacing: <c>"__Mig"__MigrationHistory"</c>.
    /// </summary>
    private static bool IsWord(Antlr4.Runtime.IToken t)
    {
        var s = t.Text;
        if (string.IsNullOrEmpty(s)) return false;
        if (s[0] == '"') return true;
        return (char.IsLetter(s[0]) || s[0] == '_')
               && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$');
    }
}
