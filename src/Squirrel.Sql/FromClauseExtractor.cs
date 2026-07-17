using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Squirrel.Core.Completion;
using Squirrel.Core.Schema;

namespace Squirrel.Sql;

/// <summary>
/// Walks a parsed statement's tree and extracts the FROM/JOIN sources as resolved
/// <see cref="TableRef"/>s (name, schema, alias, and the matched catalog relation). This is the
/// ANTLR re-implementation of the prototype's Sources resolution — but over a real parse tree.
/// </summary>
public static class FromClauseExtractor
{
    private static readonly HashSet<int> ClauseTerminators = new()
    {
        PostgreSQLParser.WHERE, PostgreSQLParser.GROUP_P, PostgreSQLParser.HAVING,
        PostgreSQLParser.ORDER, PostgreSQLParser.LIMIT, PostgreSQLParser.OFFSET,
        PostgreSQLParser.FETCH, PostgreSQLParser.WINDOW, PostgreSQLParser.UNION,
        PostgreSQLParser.INTERSECT, PostgreSQLParser.EXCEPT, PostgreSQLParser.SEMI,
        PostgreSQLParser.INTO, PostgreSQLParser.FOR,
    };

    /// <summary>
    /// Extract FROM/JOIN sources resiliently: isolate the top-level FROM-clause text by tokens and
    /// parse it in a clean synthetic statement, so garbage elsewhere (e.g. a half-typed select list
    /// or a dangling "alias.") can't hide the sources.
    /// </summary>
    public static IReadOnlyList<TableRef> Extract(string sql, ISchemaSnapshot schema)
    {
        var tokens = PgParsing.LexAll(sql)
            .Where(t => t.Channel == TokenConstants.DefaultChannel && t.Type != TokenConstants.EOF)
            .ToList();

        var fromIdx = FindTopLevelFrom(tokens);
        if (fromIdx < 0) return Array.Empty<TableRef>();

        var endIdx = FindClauseEnd(tokens, fromIdx + 1);
        var startChar = tokens[fromIdx].StopIndex + 1;
        var endChar = endIdx < tokens.Count ? tokens[endIdx].StartIndex : sql.Length;
        if (endChar < startChar) return Array.Empty<TableRef>();

        var fromText = sql[startChar..endChar];
        var parsed = PgParsing.Create("select 1 from " + fromText);
        try { return Extract(parsed.Parser.root(), schema); }
        catch { return Array.Empty<TableRef>(); }
    }

    private static int FindTopLevelFrom(IReadOnlyList<IToken> tokens)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var ty = tokens[i].Type;
            if (ty == PostgreSQLParser.OPEN_PAREN) depth++;
            else if (ty == PostgreSQLParser.CLOSE_PAREN) depth--;
            else if (ty == PostgreSQLParser.FROM && depth == 0) return i;
        }
        return -1;
    }

    private static int FindClauseEnd(IReadOnlyList<IToken> tokens, int start)
    {
        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            var ty = tokens[i].Type;
            if (ty == PostgreSQLParser.OPEN_PAREN) depth++;
            else if (ty == PostgreSQLParser.CLOSE_PAREN)
            {
                if (depth == 0) return i;   // exited an enclosing paren → FROM clause ends
                depth--;
            }
            else if (depth == 0 && ClauseTerminators.Contains(ty)) return i;
        }
        return tokens.Count;
    }

    public static IReadOnlyList<TableRef> Extract(IParseTree tree, ISchemaSnapshot schema)
    {
        var refs = new List<TableRef>();
        Walk(tree, refs, schema);
        return refs;
    }

    private static void Walk(IParseTree node, List<TableRef> refs, ISchemaSnapshot schema)
    {
        if (node is PostgreSQLParser.Table_refContext tableRef)
        {
            var relation = tableRef.relation_expr();
            var qualified = relation?.qualified_name();
            if (qualified is not null)
            {
                var (schemaName, name) = SplitQualified(qualified.GetText());
                if (!string.IsNullOrEmpty(name))
                {
                    var alias = tableRef.alias_clause()?.colid()?.GetText();
                    refs.Add(new TableRef
                    {
                        Schema = schemaName,
                        RawName = name,
                        Alias = Unquote(alias),
                        Resolved = schema.ResolveTable(schemaName, name),
                    });
                }
            }
        }

        for (var i = 0; i < node.ChildCount; i++)
            Walk(node.GetChild(i), refs, schema);
    }

    private static (string? schema, string name) SplitQualified(string text)
    {
        // qualified_name text is dot-joined: "film", "public.film", or "db.public.film".
        var parts = text.Split('.');
        var name = Unquote(parts[^1]) ?? "";
        var schema = parts.Length >= 2 ? Unquote(parts[^2]) : null;
        return (schema, name);
    }

    private static string? Unquote(string? s)
        => string.IsNullOrEmpty(s) ? s
           : (s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s);
}
