using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Bearing.Core.Completion;
using Bearing.Core.Schema;

namespace Bearing.Sql;

/// <summary>What the caret is pointing at, resolved to a catalog object (#117).</summary>
/// <param name="Schema">The relation's schema, as the catalog spells it.</param>
/// <param name="Name">The relation's name, as the catalog spells it.</param>
/// <param name="Column">A column of it, when the caret was on a column reference; else null.</param>
public sealed record DefinitionTarget(string Schema, string Name, string? Column = null)
{
    public string Label => Column is null ? $"{Schema}.{Name}" : $"{Schema}.{Name}.{Column}";
}

/// <summary>
/// Go to definition (F12) for the identifier under the caret — an alias resolving to its table, a
/// schema-qualified name to that relation, a column reference to that column (#117).
/// <para>
/// Pure: text, a caret, and a snapshot in; a catalog object or null out. The tree walk that acts on the
/// answer is the App layer's (<c>ConnectionsViewModel.RevealRelationAsync</c>), which needs a live tree;
/// this half is the part with all the cases in it, so it lives where it can be tested without one (§2.5).
/// </para>
/// <para>
/// Scoped to the statement under the caret, the same way execution is: a buffer is a script, and an alias
/// declared in the statement above is not in scope here.
/// </para>
/// </summary>
public static class GoToDefinition
{
    /// <summary>
    /// What F12 at <paramref name="caret"/> should jump to, or null when the caret is not on something the
    /// snapshot knows — a keyword, a literal, a CTE name, a function, an alias for a derived table.
    /// <para>
    /// Null rather than a guess. A jump to the wrong table is worse than no jump: it is silent, and the user
    /// then reads a schema that is not the one their query is about.
    /// </para>
    /// </summary>
    public static DefinitionTarget? Resolve(string sql, int caret, ISchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;

        // The statement under the caret, and the caret rebased into it.
        var span = StatementSplitter.StatementAt(sql, Math.Clamp(caret, 0, sql.Length));
        if (span is null) return null;
        var statement = span.Text;
        var offset = Math.Clamp(caret - span.Start, 0, statement.Length);

        var (chain, caretPart) = DottedChainAt(statement, offset);
        if (chain.Count == 0) return null;

        // Sources in scope. The caret offset is passed through so the bare name being *typed* is not
        // counted as a source (see FromClauseExtractor) — which matters here too: at `from paym|` there is
        // nothing to go to yet.
        var sources = FromClauseExtractor.Extract(statement, schema, offset);

        // Which *part* of a dotted chain the caret is on decides what F12 means. On `p` in `p.amount` the
        // user is pointing at the alias and wants the table; on `amount` they want the column. Reading only
        // the whole chain would send both to the column, which is the wrong half of the answer for the case
        // where someone is asking "what is p?".
        var onQualifier = caretPart < chain.Count - 1;

        return chain.Count switch
        {
            1 => ResolveOne(chain[0], sources, schema),
            2 when onQualifier => ResolveQualifier(chain[0], chain[1], sources, schema),
            2 => ResolveTwo(chain[0], chain[1], sources, schema),
            // schema.table.column. The caret on either of the first two parts means the relation.
            _ when onQualifier => Qualified(chain[^3], chain[^2], schema),
            _ => ResolveThree(chain[^3], chain[^2], chain[^1], schema),
        };
    }

    /// <summary>
    /// The caret is on the qualifier of a two-part reference: an alias, a table, or a schema. Every reading
    /// lands on a relation — a schema has no object of its own to go to, so the relation it qualifies is the
    /// closest thing to what was asked for.
    /// </summary>
    private static DefinitionTarget? ResolveQualifier(
        string first, string second, IReadOnlyList<TableRef> sources, ISchemaSnapshot schema)
    {
        if (AliasSource(sources, first) is { Resolved: { } aliased }) return For(aliased);
        if (Qualified(first, second, schema) is { } qualified) return qualified;
        return schema.ResolveTable(null, Unquote(first)) is { } table ? For(table) : null;
    }

    private static DefinitionTarget? Qualified(string schemaName, string table, ISchemaSnapshot schema)
        => schema.ResolveTable(Unquote(schemaName), Unquote(table)) is { } resolved ? For(resolved) : null;

    /// <summary>A bare identifier: an alias, a relation, or a column of something in scope.</summary>
    private static DefinitionTarget? ResolveOne(
        string word, IReadOnlyList<TableRef> sources, ISchemaSnapshot schema)
    {
        // An alias first: it is the one reading that is unambiguous by construction, since the query itself
        // declared it.
        if (AliasSource(sources, word) is { Resolved: { } aliased }) return For(aliased);

        // Then a relation by name, through the search_path (ResolveTable owns the casing and quoting rules).
        if (schema.ResolveTable(null, Unquote(word)) is { } table) return For(table);

        // Then a column, but only when exactly one source in scope has it. Two tables with an `id` each is
        // the commonest shape in any schema, and picking either would be a coin toss presented as an answer.
        var owners = sources
            .Select(s => s.Resolved)
            .Where(t => t is not null)
            .Distinct()
            .Where(t => ColumnOf(schema, t!, word) is not null)
            .ToList();
        if (owners.Count == 1)
            return For(owners[0]!, ColumnOf(schema, owners[0]!, word));

        return null;
    }

    /// <summary>Two parts: <c>alias.column</c>, <c>table.column</c>, or <c>schema.table</c>.</summary>
    private static DefinitionTarget? ResolveTwo(
        string first, string second, IReadOnlyList<TableRef> sources, ISchemaSnapshot schema)
    {
        // A qualifier that names a source in scope makes the second part a column of it. Tried before
        // schema.table because a query that says `p.amount` is talking about its own source, whatever a
        // schema called `p` might also contain.
        var qualified = AliasSource(sources, first) ?? NamedSource(sources, first);
        if (qualified?.Resolved is { } owner)
        {
            // A column the snapshot does not have (a computed alias, a stale snapshot) still lands on the
            // table, which is the closest true answer.
            return For(owner, ColumnOf(schema, owner, second));
        }

        // schema.table.
        if (schema.ResolveTable(Unquote(first), Unquote(second)) is { } table) return For(table);

        // A qualifier that is not in scope and not a schema: the second part may still be a relation the
        // caret is on inside a name we cannot otherwise place.
        return schema.ResolveTable(null, Unquote(second)) is { } fallback ? For(fallback) : null;
    }

    private static DefinitionTarget? ResolveThree(
        string schemaName, string table, string column, ISchemaSnapshot schema)
        => schema.ResolveTable(Unquote(schemaName), Unquote(table)) is { } resolved
            ? For(resolved, ColumnOf(schema, resolved, column))
            : null;

    private static DefinitionTarget For(TableInfo table, string? column = null)
        => new(table.Schema, table.Name, column);

    /// <summary>A source whose alias is <paramref name="word"/>.</summary>
    private static TableRef? AliasSource(IReadOnlyList<TableRef> sources, string word)
        => sources.FirstOrDefault(s => s.Alias is { } alias && Same(alias, word));

    /// <summary>A source referred to by its own (unaliased) name.</summary>
    private static TableRef? NamedSource(IReadOnlyList<TableRef> sources, string word)
        => sources.FirstOrDefault(s => s.Alias is null && Same(s.RawName, word));

    /// <summary>The column of <paramref name="table"/> named <paramref name="word"/>, as the catalog spells
    /// it, or null when it has none.</summary>
    private static string? ColumnOf(ISchemaSnapshot schema, TableInfo table, string word)
        => schema.ColumnsOf(table.Id).FirstOrDefault(c => Same(c.Name, word))?.Name;

    /// <summary>
    /// Identifier comparison: exact first, then case-insensitive.
    /// <para>
    /// Postgres folds an unquoted identifier to lower case and preserves a quoted one, so a case-insensitive
    /// match is right for the ordinary case and wrong for <c>"Id"</c> beside <c>id</c>. Exact wins where
    /// both exist, which is the best a single comparison can do without re-deciding quoting here.
    /// </para>
    /// </summary>
    private static bool Same(string a, string b)
        => string.Equals(Unquote(a), Unquote(b), StringComparison.Ordinal)
           || string.Equals(Unquote(a), Unquote(b), StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string identifier)
        => identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"'
            ? identifier[1..^1].Replace("\"\"", "\"")
            : identifier;

    /// <summary>
    /// The dotted identifier run the caret is inside — <c>["p", "amount"]</c> for <c>p.amo|unt</c> — together
    /// with which part of it the caret is on. Empty when the caret is not on an identifier at all.
    /// <para>
    /// Lexed rather than scanned by hand, for the reason §1.3 gives the redactor: only the lexer can tell a
    /// dot inside a string or a quoted identifier from one that joins two names. A caret immediately after
    /// an identifier counts as being on it, which is where it sits after double-clicking a word.
    /// </para>
    /// </summary>
    private static (List<string> Parts, int CaretPart) DottedChainAt(string statement, int offset)
    {
        var tokens = PgParsing.LexAll(statement)
            .Where(t => t.Channel == TokenConstants.DefaultChannel && t.Type != TokenConstants.EOF)
            .ToList();

        var at = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!IsIdentifier(token)) continue;
            if (offset >= token.StartIndex && offset <= token.StopIndex + 1) { at = i; break; }
        }
        if (at < 0) return ([], -1);

        // Walk out to both ends of the dotted run.
        var start = at;
        while (start - 2 >= 0 && tokens[start - 1].Type == PostgreSQLLexer.DOT && IsIdentifier(tokens[start - 2]))
            start -= 2;
        var end = at;
        while (end + 2 < tokens.Count && tokens[end + 1].Type == PostgreSQLLexer.DOT && IsIdentifier(tokens[end + 2]))
            end += 2;

        var chain = new List<string>();
        var caretPart = 0;
        for (var i = start; i <= end; i += 2)
        {
            if (i == at) caretPart = chain.Count;
            chain.Add(tokens[i].Text);
        }
        return (chain, caretPart);
    }

    /// <summary>A bare word or a double-quoted identifier — not a literal, a number or an operator. A
    /// keyword lexes as its own token type and is deliberately included: <c>from</c> is not a table, but
    /// <c>value</c>, <c>name</c> and <c>type</c> are perfectly ordinary column names that the grammar also
    /// knows as keywords, and excluding them would make F12 fail on them alone.</summary>
    private static bool IsIdentifier(IToken token)
    {
        var text = token.Text;
        if (string.IsNullOrEmpty(text)) return false;
        if (text[0] == '"') return true;
        return char.IsLetter(text[0]) || text[0] == '_';
    }
}
