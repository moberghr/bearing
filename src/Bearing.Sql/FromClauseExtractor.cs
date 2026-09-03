using Antlr4.Runtime;
using Bearing.Core.Completion;
using Bearing.Core.Schema;

namespace Bearing.Sql;

/// <summary>
/// Extracts FROM/JOIN sources as resolved <see cref="TableRef"/>s. Token-driven (not parse-tree
/// based) so it survives half-typed and structurally-broken SQL: it scans for every FROM/JOIN
/// keyword and reads the table name + optional alias that follows, at any nesting depth. That means
/// sources inside subqueries and <c>join lateral ( … )</c> blocks are found even while the enclosing
/// paren is still open — the case where a parse-tree walk drops the inner tables.
/// <para>
/// The scan is a keyword walk, so <b>which grammar lexed the buffer decides what it can see</b> — the
/// same argument <see cref="StatementSplitter"/> makes. Hence the <see cref="ISqlParseRules"/> overload:
/// the dialect-less one stays Postgres-bound for callers that have no engine to hand, matching the
/// pattern <see cref="WriteGuard"/> and <see cref="PageSql"/> already use.
/// </para>
/// </summary>
public static class FromClauseExtractor
{
    /// <summary>FROM/JOIN sources of <paramref name="sql"/>, read with the PostgreSQL grammar.</summary>
    public static IReadOnlyList<TableRef> Extract(string sql, ISchemaSnapshot schema, int? caretOffset = null)
        => Extract(PgParseRules.Instance, sql, schema, caretOffset);

    /// <param name="caretOffset">
    /// When set, the bare name the caret is editing is not reported as a source. That word is the
    /// relation being *chosen*, not one already in scope, and counting it made its own text a taken
    /// alias: completing at <c>from u</c> offered <c>users u2</c> because "u" was "already in scope",
    /// so the auto-alias depended on how much of the name had been typed when the popup opened.
    /// Only an unaliased name is dropped — an alias the query actually wrote stays in scope.
    /// </param>
    public static IReadOnlyList<TableRef> Extract(
        ISqlParseRules rules, string sql, ISchemaSnapshot schema, int? caretOffset = null)
    {
        var toks = rules.LexAll(sql)
            .Where(t => t.Channel == TokenConstants.DefaultChannel && t.Type != TokenConstants.EOF)
            .ToList();

        var refs = new List<TableRef>();

        for (var i = 0; i < toks.Count; i++)
        {
            if (toks[i].Type != rules.From && toks[i].Type != rules.Join) continue;

            var j = i + 1;
            if (j < toks.Count && toks[j].Type == rules.Lateral) j++; // JOIN LATERAL (...)

            // A comma-separated list of table refs (FROM a x, b y); each may be a name or a subquery.
            while (j < toks.Count)
            {
                if (toks[j].Type == rules.OpenParen) break; // derived table — its own FROM is scanned separately
                if (!rules.IsIdentifier(toks[j].Type)) break;

                var nameParts = new List<string> { toks[j].Text };
                var nameStart = toks[j].StartIndex;
                var nameStop = toks[j].StopIndex;
                var k = j + 1;
                while (k + 1 < toks.Count && toks[k].Type == rules.Dot && rules.IsIdentifier(toks[k + 1].Type))
                {
                    nameParts.Add(toks[k + 1].Text);
                    nameStop = toks[k + 1].StopIndex;
                    k += 2;
                }

                string? alias = null;
                if (k < toks.Count && toks[k].Type == rules.As
                    && k + 1 < toks.Count && rules.IsIdentifier(toks[k + 1].Type))
                {
                    alias = toks[k + 1].Text; k += 2;
                }
                else if (k < toks.Count && rules.IsIdentifier(toks[k].Type))
                {
                    alias = toks[k].Text; k += 1; // a bare identifier after the name is the alias
                }

                var (schemaName, name) = SplitQualified(nameParts);

                // Same span ResolveCaret treats as the word to overwrite: inside the name, or just
                // after its last character — but not immediately before it, which is an insertion
                // point in front of a source that really is in scope.
                var caretIsEditingThisName = alias is null && caretOffset is { } caret
                    && caret > nameStart && caret <= nameStop + 1;

                if (!caretIsEditingThisName)
                    refs.Add(new TableRef
                    {
                        Schema = schemaName,
                        RawName = name,
                        Alias = Unquote(alias),
                        // Quoting is kept here (and only here) so generated predicates can spell the
                        // qualifier the way the query does — `"__MigrationHistory".id`, not `.id` folded.
                        ReferenceText = alias ?? nameParts[^1],
                        Resolved = schema.ResolveTable(schemaName, name),
                    });

                if (k < toks.Count && toks[k].Type == rules.Comma) { j = k + 1; continue; }
                break;
            }
        }

        return Dedupe(refs);
    }

    private static (string? schema, string name) SplitQualified(IReadOnlyList<string> parts)
    {
        var name = Unquote(parts[^1]) ?? "";
        var schema = parts.Count >= 2 ? Unquote(parts[^2]) : null;
        return (schema, name);
    }

    private static IReadOnlyList<TableRef> Dedupe(List<TableRef> refs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TableRef>();
        foreach (var r in refs)
            if (seen.Add($"{r.Schema}.{r.RawName} {r.EffectiveName}"))
                result.Add(r);
        return result;
    }

    /// <summary>Still Postgres' unquoting, like <c>CompletionEngine.Q</c> — changing it changes what
    /// T-SQL resolves, which is the next batch's business, not the seam's.</summary>
    private static string? Unquote(string? s) => s is null ? null : PgIdentifier.Unquote(s);
}
