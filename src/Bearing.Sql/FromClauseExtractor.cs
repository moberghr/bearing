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
/// </summary>
public static class FromClauseExtractor
{
    public static IReadOnlyList<TableRef> Extract(string sql, ISchemaSnapshot schema)
    {
        var toks = PgParsing.LexAll(sql)
            .Where(t => t.Channel == TokenConstants.DefaultChannel && t.Type != TokenConstants.EOF)
            .ToList();

        var refs = new List<TableRef>();

        for (var i = 0; i < toks.Count; i++)
        {
            if (toks[i].Type is not (PostgreSQLParser.FROM or PostgreSQLParser.JOIN)) continue;

            var j = i + 1;
            if (j < toks.Count && toks[j].Type == PostgreSQLParser.LATERAL_P) j++; // JOIN LATERAL (...)

            // A comma-separated list of table refs (FROM a x, b y); each may be a name or a subquery.
            while (j < toks.Count)
            {
                if (toks[j].Type == PostgreSQLParser.OPEN_PAREN) break; // derived table — its own FROM is scanned separately
                if (!IsName(toks[j])) break;

                var nameParts = new List<string> { toks[j].Text };
                var k = j + 1;
                while (k + 1 < toks.Count && toks[k].Type == PostgreSQLParser.DOT && IsName(toks[k + 1]))
                {
                    nameParts.Add(toks[k + 1].Text);
                    k += 2;
                }

                string? alias = null;
                if (k < toks.Count && toks[k].Type == PostgreSQLParser.AS
                    && k + 1 < toks.Count && IsName(toks[k + 1]))
                {
                    alias = toks[k + 1].Text; k += 2;
                }
                else if (k < toks.Count && toks[k].Type is PostgreSQLParser.Identifier or PostgreSQLParser.QuotedIdentifier)
                {
                    alias = toks[k].Text; k += 1; // a bare identifier after the name is the alias
                }

                var (schemaName, name) = SplitQualified(nameParts);
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

                if (k < toks.Count && toks[k].Type == PostgreSQLParser.COMMA) { j = k + 1; continue; }
                break;
            }
        }

        return Dedupe(refs);
    }

    /// <summary>Only true identifiers (bare or quoted) name a table or alias here.</summary>
    private static bool IsName(IToken t)
        => t.Type is PostgreSQLParser.Identifier or PostgreSQLParser.QuotedIdentifier;

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

    private static string? Unquote(string? s) => s is null ? null : PgIdentifier.Unquote(s);
}
