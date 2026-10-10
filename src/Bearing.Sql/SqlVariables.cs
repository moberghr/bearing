using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>One <c>:name</c> (or <c>:'name'</c>) reference in a script, with where it sits.</summary>
/// <param name="Name">The variable's name, without the colon or quotes.</param>
/// <param name="Start">Offset of the colon.</param>
/// <param name="Length">Length of the whole reference, colon and quotes included.</param>
/// <param name="Quoted">True for <c>:'name'</c> — the value goes in as a string literal rather than as
/// typed.</param>
public sealed record SqlVariableRef(string Name, int Start, int Length, bool Quoted);

/// <summary>
/// DBeaver-style script variables: <c>:name</c> in a script is a hole the user fills in before it runs.
/// The value is spliced in <b>as typed</b> — <c>42</c>, <c>'abc'</c>, <c>now()</c> or a table name all
/// work, which is the point of a text substitution — and <c>:'name'</c> (psql's spelling) splices it in
/// as a quoted string literal instead, so text values need no quoting by hand.
/// <para>
/// Text substitution rather than driver parameters, deliberately. A bound parameter would arrive at
/// Postgres as an untyped value and break <c>where id = $1</c> on an integer column the moment it was
/// sent as text; it could not stand in for an identifier at all; and every later trip to the server for
/// the same result — the next page, the total count, an export — would have to carry the bindings along.
/// Substituted text is just the SQL that ran: the write guard reads it (so a value of <c>1; delete …</c>
/// is caught like any other write), the query log records it, and paging re-runs it unchanged.
/// </para>
/// <para>
/// Lexer-based per dialect, for the same reason <see cref="SqlRedactor"/> is: a colon inside a string,
/// a comment, a dollar-quoted body or a delimited name is not a variable, and neither is either half of
/// <c>::</c> (a Postgres cast, a T-SQL <c>SCHEMA::dbo</c>) or plpgsql's <c>:=</c>. Postgres array slices
/// (<c>a[1:n]</c>) are skipped too — the colon there is the slice. The colon must touch the name;
/// <c>: name</c> is not a reference.
/// </para>
/// </summary>
public static class SqlVariables
{
    /// <summary>Every variable reference in <paramref name="sql"/>, in order of appearance.</summary>
    public static IReadOnlyList<SqlVariableRef> Find(ISqlDialect dialect, string? sql)
        => string.IsNullOrEmpty(sql) || sql.IndexOf(':') < 0 ? [] : dialect.FindVariables(sql);

    /// <summary>The distinct variable names, in order of first appearance. Names compare
    /// case-insensitively — <c>:Id</c> and <c>:id</c> are one hole, as an unquoted SQL name would be.</summary>
    public static IReadOnlyList<string> Names(ISqlDialect dialect, string? sql)
        => Find(dialect, sql).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// <paramref name="sql"/> with every reference replaced by its value. A reference with no entry in
    /// <paramref name="values"/> is left as written, so the server reports it rather than this guessing.
    /// </summary>
    public static string Substitute(
        ISqlDialect dialect, string sql, IReadOnlyDictionary<string, string> values)
    {
        var refs = Find(dialect, sql);
        if (refs.Count == 0) return sql;

        var lookup = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(sql.Length);
        var copied = 0;
        foreach (var r in refs)
        {
            if (!lookup.TryGetValue(r.Name, out var value)) continue;
            sb.Append(sql, copied, r.Start - copied);
            sb.Append(r.Quoted ? dialect.TextLiteral(value) : value);
            copied = r.Start + r.Length;
        }
        sb.Append(sql, copied, sql.Length - copied);
        return sb.ToString();
    }

    /// <summary>The Postgres reading, over the vendored lexer.</summary>
    internal static IReadOnlyList<SqlVariableRef> FindPostgres(string sql)
    {
        var tokens = PgParsing.LexAll(sql)
            .Where(t => t.Type != TokenConstants.EOF && t.Channel == TokenConstants.DefaultChannel)
            .ToList();
        var found = new List<SqlVariableRef>();
        var brackets = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Type == PostgreSQLLexer.OPEN_BRACKET) { brackets++; continue; }
            if (t.Type == PostgreSQLLexer.CLOSE_BRACKET) { if (brackets > 0) brackets--; continue; }
            if (brackets > 0) continue;

            // The grammar lexes `:name` as one token already (it is psql's own spelling). Its name may
            // carry `$`, which IsName refuses — such a token is left alone rather than half-substituted.
            if (t.Type == PostgreSQLLexer.PLSQLVARIABLENAME)
            {
                if (IsName(t.Text[1..]))
                    found.Add(new SqlVariableRef(t.Text[1..], t.StartIndex, t.StopIndex + 1 - t.StartIndex, false));
                continue;
            }

            // COLON alone: `::` lexes as TYPECAST and `:=` as COLON_EQUALS, so neither reaches here.
            if (t.Type != PostgreSQLLexer.COLON || i + 1 >= tokens.Count) continue;

            var next = tokens[i + 1];
            if (next.StartIndex != t.StopIndex + 1) continue;           // `: name` is not a reference

            if (IsName(next.Text))
                found.Add(new SqlVariableRef(next.Text, t.StartIndex, next.StopIndex + 1 - t.StartIndex, false));
            else if (next.Type == PostgreSQLLexer.StringConstant && QuotedName(next.Text) is { } name)
                found.Add(new SqlVariableRef(name, t.StartIndex, next.StopIndex + 1 - t.StartIndex, true));
        }
        return found;
    }

    /// <summary>The T-SQL reading, over <see cref="TSqlScanner"/>.</summary>
    internal static IReadOnlyList<SqlVariableRef> FindTSql(string sql)
    {
        var tokens = TSqlScanner.Tokenize(sql);
        var found = new List<SqlVariableRef>();

        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TSqlTokenKind.Punctuation || t.Text != ":") continue;
            // Either half of `SCHEMA::dbo`: the scanner emits two single-colon tokens.
            if (i > 0 && tokens[i - 1] is { Kind: TSqlTokenKind.Punctuation, Text: ":" } prev
                && prev.Start + 1 == t.Start) continue;

            var next = tokens[i + 1];
            if (next.Start != t.Start + 1) continue;

            if (next.Kind == TSqlTokenKind.Word && IsName(next.Text))
                found.Add(new SqlVariableRef(next.Text, t.Start, next.Start + next.Length - t.Start, false));
            else if (next.Kind == TSqlTokenKind.Text && QuotedName(next.Text) is { } name)
                found.Add(new SqlVariableRef(name, t.Start, next.Start + next.Length - t.Start, true));
        }
        return found;
    }

    /// <summary>A plain name — letter or underscore, then letters, digits and underscores. Keywords
    /// count: <c>:date</c> and <c>:user</c> are perfectly good variable names.</summary>
    private static bool IsName(string text)
        => text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_')
           && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>The name inside <c>'name'</c>, or null when the literal holds anything else.</summary>
    private static string? QuotedName(string text)
        => text.Length > 2 && text[0] == '\'' && text[^1] == '\'' && IsName(text[1..^1]) ? text[1..^1] : null;
}
