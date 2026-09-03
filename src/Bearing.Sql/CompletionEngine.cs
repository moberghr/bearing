using Antlr4.Runtime.Tree;
using Antlr4CodeCompletion.Core.CodeCompletion;
using Bearing.Core.Completion;
using Bearing.Core.Schema;

namespace Bearing.Sql;

/// <summary>
/// Schema-aware SQL completion built on ANTLR + antlr4-c3. Pure and synchronous: given the SQL
/// text, caret offset, and a schema snapshot, returns ranked suggestions and the span they replace.
/// </summary>
public sealed class CompletionEngine : ICompletionEngine
{
    private readonly Func<ISqlDialect> _dialect;

    /// <summary>
    /// <paramref name="dialect"/> is asked once per request rather than captured as a value: one engine
    /// serves the whole window, and which engine the buffer is written in changes with the selected tab.
    /// Defaults to Postgres so every caller that has no tab to ask — the pinning tests, and the
    /// Postgres-bound statics' own idiom — keeps working unchanged.
    /// </summary>
    public CompletionEngine(Func<ISqlDialect>? dialect = null)
        => _dialect = dialect ?? (() => PostgresDialect.Instance);

    public CompletionResult Complete(string sql, int caretOffset, ISchemaSnapshot schema)
    {
        caretOffset = Math.Clamp(caretOffset, 0, sql.Length);

        // Nothing in the catalog or the grammar belongs inside 'text' — it is data, and a popup over it is
        // noise you have to dismiss. Double-quoted spans are deliberately still completed: those are quoted
        // *identifiers*, which is precisely where a table or column name goes.
        if (SqlStringLiterals.Contains(sql, caretOffset))
            return new CompletionResult(Array.Empty<Suggestion>(), caretOffset, 0);

        // The one place the engine learns which grammar it is reading. Everything below works in terms
        // of roles and intents, which is what lets one engine answer for both dialects. The dialect
        // itself is kept, not just its rules: how a name is *quoted* on the way out is its business too.
        var dialect = _dialect();
        var rules = dialect.ParseRules;

        var parsed = rules.Parse(sql);
        parsed.Tokens.Fill();
        var caret = ResolveCaret(parsed.Tokens, caretOffset);
        var aliasSlot = CaretIsInAliasSlot(rules, schema, parsed.Tokens.GetTokens(), caret.TokenIndex);

        parsed.PrimeForCompletion();

        // FROM/JOIN scope is extracted resiliently (token-isolated), independent of select-list garbage.
        // The caret is passed so the half-typed name being completed isn't counted as a source of its
        // own — that is what made the auto-alias depend on how far the name had been typed (#42).
        var sources = FromClauseExtractor.Extract(rules, sql, schema, caretOffset);

        var core = new CodeCompletionCore(parsed.Parser, rules.PreferredRules.ToHashSet(),
            rules.IgnoredTokens.ToHashSet());
        var candidates = core.CollectCandidates(caret.TokenIndex, context: null);
        var intents = candidates.Rules.Keys.Select(rules.Classify).ToHashSet();

        // A half-typed alias is a name the user is inventing — nothing in the catalog or the grammar
        // belongs there, and whatever sat under Enter would overwrite it. An *empty* alias slot still
        // gets keywords (as / join / where), which is what actually follows a named source.
        // …unless the caret is at the start of a line, where the more likely reading is a new statement being
        // typed rather than an alias for the relation on the line above (#68). The alias rule keys off the
        // previous meaningful token, which does not care that a newline came between.
        var statementStart = StatementStartHint.AtLineStart(sql, caretOffset);
        if (aliasSlot && !statementStart && caret.ReplacementLength > 0)
            return new CompletionResult(Array.Empty<Suggestion>(), caret.ReplacementStart, caret.ReplacementLength);

        var suggestions = new List<Suggestion>();
        var qualifier = rules.QualifierBefore(sql, caretOffset);

        if (qualifier is not null && IsResolvedSource(sources, qualifier))
        {
            // After "alias." only that alias's columns (and, where a predicate can actually begin, the
            // FK equality joining it to an in-scope table) make sense — never tables/joins/keywords,
            // regardless of how c3 classifies the caret in the surrounding (often broken) SQL.
            if (FkPredicateFits(rules, parsed.Tokens.GetTokens(), caret.TokenIndex))
                suggestions.AddRange(FkPredicateSuggestions(dialect, schema, sources, qualifier));
            suggestions.AddRange(ColumnSuggestions(dialect, schema, sources, qualifier));
        }
        else if (qualifier is not null && SchemaNamed(schema, qualifier) is { } qualifierSchema)
        {
            // "public." is a schema qualifier, not an alias — an in-scope alias of the same name wins
            // (checked above), otherwise the only sensible answer is that schema's relations. This
            // branch used to fall through to the column path and produce an empty popup.
            suggestions.AddRange(TableSuggestions(dialect, schema, sources, onlySchema: qualifierSchema));
        }
        else if (qualifier is null)
        {
            // An alias slot is still a "table position" to c3 (an alias is just an identifier), but the
            // one thing that cannot belong there is another relation — offering them made accepting one
            // overwrite the alias: `select * from film f` → `select * from film film f2`. Keywords still
            // come through, which is what actually follows a named source (as / join / where).
            if (intents.Contains(CompletionIntent.TablePosition) && !aliasSlot)
            {
                suggestions.AddRange(TableSuggestions(dialect, schema, sources));
                suggestions.AddRange(SchemaSuggestions(dialect, schema));
                // Only where a join can actually attach, and carrying whatever keyword the caret is still
                // missing — accepting one after a bare source used to emit `from users u orders o on …`.
                if (sources.Count > 0
                    && JoinKeywordPrefix(rules, parsed.Tokens.GetTokens(), caret.TokenIndex) is { } joinPrefix)
                    suggestions.AddRange(JoinSuggestions(dialect, schema, sources, joinPrefix));
            }

            if (intents.Contains(CompletionIntent.ColumnPosition))
                suggestions.AddRange(ColumnSuggestions(dialect, schema, sources, qualifier: null));

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

            // At a line start, add the keywords that could open a statement. Added rather than substituted:
            // a line holding one bare word is genuinely ambiguous — `where` continuing the query above and
            // `select` starting a new one are both plausible — so replacing the scope with the current line,
            // as #68 first proposed, would have traded one missing answer for the other. The grammar's own
            // candidates keep the higher priority, and the typed fragment narrows both (SuggestionRanker).
            if (statementStart)
            {
                var already = suggestions
                    .Where(s => s.Kind == SuggestionKind.Keyword)
                    .Select(s => s.DisplayText)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var kw in StatementSplitter.StatementStarters)
                {
                    if (!already.Add(kw)) continue;
                    suggestions.Add(new Suggestion
                    {
                        DisplayText = kw,
                        ReplacementText = kw,
                        Kind = SuggestionKind.Keyword,
                        Priority = 0,
                    });
                }
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
        var rules = _dialect().ParseRules;
        var parsed = rules.Parse(sql);
        parsed.Tokens.Fill();
        var caret = ResolveCaret(parsed.Tokens, caretOffset);
        parsed.PrimeForCompletion();

        var core = new CodeCompletionCore(parsed.Parser, rules.PreferredRules.ToHashSet(),
            rules.IgnoredTokens.ToHashSet());
        var candidates = core.CollectCandidates(caret.TokenIndex, context: null);
        return candidates.Rules.Keys.Select(rules.Classify).ToHashSet();
    }

    // ---- Table suggestions (auto-aliased, like the prototype) --------------------------------

    /// <param name="onlySchema">When set, only that schema's relations, and inserted bare — the caret
    /// already sits after <c>schema.</c>.</param>
    private static IEnumerable<Suggestion> TableSuggestions(
        ISqlDialect dialect, ISchemaSnapshot schema, IReadOnlyList<TableRef> sources,
        string? onlySchema = null)
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
                ? $"{Q(dialect, t.Schema)}.{Q(dialect, t.Name)}"
                : Q(dialect, t.Name);
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
    private static IEnumerable<Suggestion> SchemaSuggestions(ISqlDialect dialect, ISchemaSnapshot schema)
    {
        var reachable = new HashSet<string>(schema.SearchPath, StringComparer.OrdinalIgnoreCase);
        foreach (var name in schema.Schemas)
            yield return new Suggestion
            {
                DisplayText = name,
                FilterText = name,
                DetailText = "schema",
                // The trailing dot leaves the caret where the relation list continues.
                ReplacementText = $"{Q(dialect, name)}.",
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
    private static bool FkPredicateFits(
        ISqlParseRules rules, IList<Antlr4.Runtime.IToken> toks, int caretTokenIndex)
    {
        // Walk back to the qualifier: the caret may sit on the column being typed, on the dot, or between.
        var i = PrevMeaningful(toks, caretTokenIndex);
        if (i >= 0 && IsNameToken(rules, toks[i])) i = PrevMeaningful(toks, i - 1);
        if (i < 0 || toks[i].Type != rules.Dot) return false;

        i = PrevMeaningful(toks, i - 1);
        if (i < 0 || !IsNameToken(rules, toks[i])) return false;

        var before = PrevMeaningful(toks, i - 1);
        if (before < 0) return false;
        var opener = toks[before].Type;
        return opener == rules.On || opener == rules.Where || opener == rules.And || opener == rules.Or
            || opener == rules.Not || opener == rules.Having || opener == rules.OpenParen;
    }

    /// <summary>
    /// True when the caret sits where a source's alias goes, the relation before it already being named:
    /// <c>from film f|</c>, <c>from film |</c>, <c>from film as f|</c>, <c>from public.film f|</c>. Keyed
    /// off the token before the caret rather than the parse tree, so it survives half-typed SQL like the
    /// rest of this file.
    /// </summary>
    private static bool CaretIsInAliasSlot(
        ISqlParseRules rules, ISchemaSnapshot schema, IList<Antlr4.Runtime.IToken> toks, int caretTokenIndex)
    {
        var i = PrevMeaningful(toks, caretTokenIndex - 1);
        if (i < 0) return false;

        if (toks[i].Type == rules.As)
        {
            i = PrevMeaningful(toks, i - 1);
            if (i < 0) return false;
        }
        if (!IsNameToken(rules, toks[i])) return false;

        // Walk back over a qualified name (schema.relation), collecting its parts.
        var parts = new List<string> { toks[i].Text };
        var before = PrevMeaningful(toks, i - 1);
        while (before >= 0 && toks[before].Type == rules.Dot)
        {
            var name = PrevMeaningful(toks, before - 1);
            if (name < 0 || !IsNameToken(rules, toks[name])) break;
            parts.Insert(0, toks[name].Text);
            before = PrevMeaningful(toks, name - 1);
        }

        // Only a FROM/JOIN list puts a relation name there; anything else (a select-list expression, a
        // WHERE) is not an alias slot even though it may end in an identifier.
        if (before < 0) return false;
        var opener = toks[before].Type;
        if (!(opener == rules.From || opener == rules.Join || opener == rules.Comma
              || opener == rules.Lateral))
            return false;

        var relation = rules.Unquote(parts[^1]);
        var schemaName = parts.Count >= 2 ? rules.Unquote(parts[^2]) : null;
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

    private static bool IsNameToken(ISqlParseRules rules, Antlr4.Runtime.IToken t)
        => rules.IsIdentifier(t.Type);

    private static bool IsResolvedSource(IReadOnlyList<TableRef> sources, string qualifier)
        => sources.Any(s => s.Resolved is not null
                            && string.Equals(s.EffectiveName, qualifier, StringComparison.OrdinalIgnoreCase));

    /// <summary>The schema named <paramref name="qualifier"/> as the catalog spells it, or null.</summary>
    private static string? SchemaNamed(ISchemaSnapshot schema, string qualifier)
        => schema.Schemas.FirstOrDefault(s => string.Equals(s, qualifier, StringComparison.OrdinalIgnoreCase));

    // ---- FK-driven smart joins (bidirectional) -----------------------------------------------

    /// <summary>
    /// What an FK-join insertion has to type for the user at this caret, or null when a join suggestion does
    /// not belong here at all. The engine offered these on any table position with a source in scope and
    /// never inspected the token before the caret, so accepting one directly after a completed source
    /// produced <c>from users u orders o on …</c> — no <c>join</c>, invalid SQL (#75).
    /// <list type="bullet">
    /// <item><c>… join |</c> → <c>""</c>: the keyword is already there.</item>
    /// <item><c>… left |</c>, <c>… left outer |</c>, <c>inner</c>, <c>full</c> → <c>"join "</c>: the
    /// qualifier is typed and only the keyword is missing.</item>
    /// <item><c>… cross |</c>, <c>… natural |</c> → null: those joins take no <c>on</c> clause, so an
    /// FK-equality suggestion has no valid shape to be inserted in.</item>
    /// <item><c>… users u |</c> → <c>"join "</c>: a completed source, which is the reported case.</item>
    /// <item><c>… users u, |</c> → null: a comma-separated source cannot carry an <c>on</c> clause, and the
    /// predicate belongs in the WHERE. Offering a join here has no correct insertion.</item>
    /// </list>
    /// Keyed off the token before the caret rather than the parse tree, like every other caret-context check
    /// in this file, so it survives the half-typed SQL completion actually runs against.
    /// </summary>
    private static string? JoinKeywordPrefix(
        ISqlParseRules rules, IList<Antlr4.Runtime.IToken> toks, int caretTokenIndex)
    {
        var i = PrevMeaningful(toks, caretTokenIndex - 1);
        if (i < 0) return null;

        var type = toks[i].Type;
        if (type == rules.Join) return "";
        if (rules.JoinQualifiers.Contains(type)) return "join ";
        // CROSS JOIN and NATURAL JOIN take no ON clause at all, so an FK-equality suggestion has no
        // valid shape here — `cross join orders o on …` is a syntax error, not a missing keyword.
        if (rules.OnlessJoinQualifiers.Contains(type)) return null;
        if (type == rules.Comma) return null;
        return IsNameToken(rules, toks[i]) ? "join " : null;
    }

    /// <param name="joinPrefix">The keyword the caret is missing — see <see cref="JoinKeywordPrefix"/>.
    /// Empty when the user has already typed <c>join</c>.</param>
    private static IEnumerable<Suggestion> JoinSuggestions(
        ISqlDialect dialect, ISchemaSnapshot schema, IReadOnlyList<TableRef> sources, string joinPrefix)
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
                    preds.Add($"{alias}.{Q(dialect, ColumnName(schema, otherOid, otherCols[i]))}"
                              + $" = {srcAlias}.{Q(dialect, ColumnName(schema, srcOid, srcCols[i]))}");
                var predicate = string.Join(" and ", preds);

                yield return new Suggestion
                {
                    DisplayText = other.Name,
                    FilterText = other.Name,
                    DetailText = $"join → {SourceLabel(src)}",
                    TrailingText = predicate,
                    ReplacementText = $"{joinPrefix}{Q(dialect, other.Name)} {alias} on {predicate}",
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
        ISqlDialect dialect, ISchemaSnapshot schema, IReadOnlyList<TableRef> sources, string qualifier)
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
                    preds.Add($"{Q(dialect, ColumnName(schema, ownerOid, ownerCols[i]))}"
                              + $" = {other.EffectiveRef}.{Q(dialect, ColumnName(schema, otherOid, otherCols[i]))}");
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
        ISqlDialect dialect, ISchemaSnapshot schema, IReadOnlyList<TableRef> sources, string? qualifier)
    {
        if (qualifier is not null)
        {
            var owner = sources.FirstOrDefault(s =>
                string.Equals(s.EffectiveName, qualifier, StringComparison.OrdinalIgnoreCase));
            if (owner?.Resolved is null) yield break;
            foreach (var c in schema.ColumnsOf(owner.Resolved.Id))
                yield return ColumnSuggestion(dialect, c, owner.EffectiveName);
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
                    yield return ColumnSuggestion(dialect, c, src.EffectiveName,
                        qualifier: src.Alias is not null ? src.EffectiveRef : null);
            yield break;
        }

        // No FROM yet: offer every column (M3 fallback).
        foreach (var t in schema.Tables)
            foreach (var c in schema.ColumnsOf(t.Id))
                yield return ColumnSuggestion(dialect, c, t.Name);
    }

    /// <param name="qualifier">Prefix for the inserted text (<c>f.id</c>); null inserts the bare column,
    /// which is what the <c>alias.</c> path needs — the qualifier is already typed there.</param>
    private static Suggestion ColumnSuggestion(
        ISqlDialect dialect, ColumnInfo c, string owner, string? qualifier = null) => new()
        {
            DisplayText = c.Name,
            FilterText = c.Name,
            DetailText = owner,
            ReplacementText = qualifier is null ? Q(dialect, c.Name) : $"{qualifier}.{Q(dialect, c.Name)}",
            Kind = SuggestionKind.Column,
            Priority = c.IsPrimaryKey ? 9 : 8,
            Description = $"{owner}.{c.Name} : {c.DataType}",
        };

    /// <summary>Quote a catalog name for insertion when the bare form wouldn't round-trip
    /// (<c>__MigrationHistory</c>, <c>order</c>); a name that already means itself stays bare so the
    /// completed SQL reads the way it would if it were typed by hand.
    /// <para>
    /// <b>The selected connection's rule, not Postgres'.</b> The two engines disagree about the common
    /// case rather than an edge of it: Postgres folds an unquoted name to lower case, so anything not
    /// already lower case has to be quoted, while SQL Server preserves case. Running T-SQL through the
    /// Postgres rule therefore quoted <em>every PascalCase name in the catalog</em> — and PascalCase is
    /// T-SQL's own convention, so completion emitted <c>"Customers"</c> where the user wanted
    /// <c>Customers</c>, and <c>"Order Details"</c> where T-SQL needs <c>[Order Details]</c>. The first
    /// is merely unreadable; the second is a syntax error.
    /// </para></summary>
    private static string Q(ISqlDialect dialect, string identifier) => dialect.QuoteIfNeeded(identifier);

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
