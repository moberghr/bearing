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

/// <summary>What <see cref="SqlVariables.Parameterize"/> made of a script.</summary>
/// <param name="Sql">The script with each <c>:name</c> replaced by its <c>@bearing_name</c> placeholder.</param>
/// <param name="Names">The variables, distinct, in order of first appearance, as first written.</param>
/// <param name="QuotedNames">Those written <c>:'name'</c> at least once — bound as text whatever they look like.</param>
public sealed record ParameterizedSql(string Sql, IReadOnlyList<string> Names, IReadOnlySet<string> QuotedNames);

/// <summary>
/// <c>:name</c> placeholders the user fills in before a script runs (#103, DBeaver style). The values are
/// <b>bound as driver parameters</b>, never spliced into the text: <see cref="Parameterize"/> rewrites each
/// reference to a named placeholder and the executor binds the value to it, so a value can never change what
/// the statement says, and the query log keeps the text with its placeholders and the values beside it.
/// <para>
/// What that gives up, deliberately: a value can't stand in for a table or column name, only for a value.
/// <c>:'name'</c> (psql's spelling) is still accepted and means "this one is text", even when it looks like a
/// number.
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

    /// <summary>The prefix on every placeholder <see cref="Parameterize"/> writes. Long enough not to meet a
    /// T-SQL local variable or a MySQL user variable of the user's own (<c>@id</c>), which would otherwise be
    /// bound in their place.</summary>
    public const string PlaceholderPrefix = "bearing_";

    /// <summary>The driver-side name of variable <paramref name="name"/>: <c>bearing_id</c>, written into the
    /// SQL as <c>@bearing_id</c>. Lower-cased, because <c>:Id</c> and <c>:id</c> are one variable.</summary>
    public static string PlaceholderName(string name) => PlaceholderPrefix + name.ToLowerInvariant();

    /// <summary>
    /// <paramref name="sql"/> with every reference rewritten to its driver placeholder (<c>@bearing_id</c> —
    /// the spelling Npgsql, SqlClient and MySqlConnector all bind by name), the distinct names in order of
    /// first appearance, and which of them were written <c>:'name'</c> somewhere (those bind as text).
    /// </summary>
    public static ParameterizedSql Parameterize(ISqlDialect dialect, string sql)
    {
        var refs = Find(dialect, sql);
        if (refs.Count == 0) return new ParameterizedSql(sql, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var names = new List<string>();
        var quoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(sql.Length);
        var copied = 0;
        foreach (var r in refs)
        {
            if (!names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)) names.Add(r.Name);
            if (r.Quoted) quoted.Add(r.Name);
            sb.Append(sql, copied, r.Start - copied);
            sb.Append('@').Append(PlaceholderName(r.Name));
            copied = r.Start + r.Length;
        }
        sb.Append(sql, copied, sql.Length - copied);
        return new ParameterizedSql(sb.ToString(), names, quoted);
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

    /// <summary>
    /// The MySQL / MariaDB reading, over <see cref="MySqlScanner"/>. The scanner already keeps strings,
    /// comments, backticked names and <c>@user</c> variables out of the way; two colons are left to rule out
    /// here: <c>:=</c> (its <c>=</c> isn't a name, so it never matches) and a label written flush against its
    /// keyword, <c>l1:loop</c> — a colon straight after a word is a label's, never a variable's.
    /// </summary>
    internal static IReadOnlyList<SqlVariableRef> FindMySql(string sql)
    {
        var tokens = MySqlScanner.Tokenize(sql);
        var found = new List<SqlVariableRef>();

        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != MySqlTokenKind.Punctuation || t.Text != ":") continue;
            if (i > 0 && tokens[i - 1] is { Kind: MySqlTokenKind.Word } prev
                && prev.Start + prev.Length == t.Start) continue;

            var next = tokens[i + 1];
            if (next.Start != t.Start + 1) continue;

            if (next.Kind == MySqlTokenKind.Word && IsName(next.Text))
                found.Add(new SqlVariableRef(next.Text, t.Start, next.Start + next.Length - t.Start, false));
            else if (next.Kind == MySqlTokenKind.Text && QuotedName(next.Text) is { } name)
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
