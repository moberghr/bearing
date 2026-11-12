using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Bearing.Core.Data;

namespace Bearing.Sql;

/// <summary>One <c>@set name = value</c> line in a script.</summary>
/// <param name="Name">The variable, without the colon it is referenced by.</param>
/// <param name="Value">The value's text, quotes removed; null for SQL NULL.</param>
/// <param name="Kind">What the value is — see <see cref="ScriptDirectives.ParseValue"/>.</param>
/// <param name="Start">Offset of the line's first character (its indentation included).</param>
/// <param name="Length">The line's length, line break excluded.</param>
/// <param name="HasValue">False for <c>@set name =</c> with nothing after it: the line is still Bearing's (and
/// still removed), but it gives no value — the variable is asked for. <c>''</c> is how to give empty text.</param>
public sealed record ScriptDirective(
    string Name, string? Value, QueryParameterKind Kind, int Start, int Length, bool HasValue = true)
{
    public int End => Start + Length;
}

/// <summary>
/// <c>@set name = value</c> — DBeaver's spelling for giving a <c>:name</c> variable its value in the script
/// itself (#103), so a query reopened from history runs with the values it ran with, and a script can carry
/// its own inputs. Bearing reads these lines and removes them before anything is sent: the server never sees
/// one.
/// <para>
/// <b>Recognised only where it can't be SQL:</b> a line whose first token is <c>@set</c>, then a plain name,
/// then <c>=</c>. Never inside a string, a comment or a dollar-quoted body (each dialect's own lexer decides,
/// as for <see cref="SqlVariables"/>). <c>@set x = 1</c> is not a valid statement start on Postgres, SQL
/// Server or MySQL, so no real statement is taken for one — T-SQL's <c>set @set = 1</c> and a
/// <c>@set = …</c> line inside an UPDATE's SET list have no name before the <c>=</c>, and MySQL's
/// <c>@set := 1</c> no plain <c>=</c>.
/// </para>
/// </summary>
public static partial class ScriptDirectives
{
    // The line's shape. The value runs to the end of the line; what it means is ParseValue's business.
    [GeneratedRegex(@"(?im)^[ \t]*@set[ \t]+([a-z_][a-z0-9_]*)[ \t]*=(?!=)([^\r\n]*)", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    /// <summary>Every <c>@set</c> line in <paramref name="sql"/>, in order.</summary>
    public static IReadOnlyList<ScriptDirective> Find(ISqlDialect dialect, string? sql)
        => string.IsNullOrEmpty(sql) || sql.IndexOf("@set", StringComparison.OrdinalIgnoreCase) < 0
            ? []
            : dialect.FindDirectives(sql);

    /// <summary>
    /// Whether <paramref name="raw"/> gives a value at all. Blank — nothing, or only a <c>;</c> — does not: a
    /// value left out is not taken for empty text, which would run (and, on a numeric column, fail on the
    /// server) without anyone having chosen it. Empty text is <c>''</c>; NULL is <c>null</c>.
    /// </summary>
    public static bool IsGiven(string? raw) => raw is not null && raw.Trim().TrimEnd(';').Trim().Length > 0;

    /// <summary>
    /// What a value written after <c>=</c> — or typed into the prompt — means. Surrounding blanks and one
    /// trailing <c>;</c> are dropped. Then:
    /// <list type="bullet">
    ///   <item><c>'text'</c> (with <c>''</c> for a quote inside) is that text, whatever it looks like —
    ///     <c>'01234'</c> stays a string, <c>'null'</c> is four letters.</item>
    ///   <item><c>null</c>, unquoted, is SQL NULL.</item>
    ///   <item>A plain decimal number (<c>42</c>, <c>-1.5</c>, no leading zero) is a number.</item>
    ///   <item>Anything else is the text as written: <c>2005-06-01</c> needs no quotes.</item>
    /// </list>
    /// </summary>
    public static (string? Value, QueryParameterKind Kind) ParseValue(string raw)
    {
        var text = raw.Trim();
        if (text.EndsWith(';')) text = text[..^1].TrimEnd();

        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'' && Unquote(text) is { } unquoted)
            return (unquoted, QueryParameterKind.Text);
        if (text.Equals("null", StringComparison.OrdinalIgnoreCase))
            return (null, QueryParameterKind.Null);
        if (NumberPattern().IsMatch(text))
            return (text, QueryParameterKind.Number);
        return (text, QueryParameterKind.Text);
    }

    /// <summary>
    /// A value as it would be written after <c>@set name =</c>, so that <see cref="ParseValue"/> reads back
    /// exactly what was given: text is always quoted, so it can't come back as a number or a null. Null when
    /// it can't be written on one line (text holding a line break) — that variable is left for the prompt.
    /// </summary>
    public static string? FormatValue(string? value, QueryParameterKind kind) => kind switch
    {
        QueryParameterKind.Null => "null",
        QueryParameterKind.Number when value is not null && NumberPattern().IsMatch(value) => value,
        _ when value is null => "null",
        _ when value.Contains('\n') || value.Contains('\r') => null,
        _ => "'" + value.Replace("'", "''") + "'",
    };

    /// <summary>
    /// The <c>@set</c> lines that give <paramref name="values"/>, one per variable in order, each ending in a
    /// line break — what opening a query from history puts above its SQL. A value <see cref="FormatValue"/>
    /// can't write is left out, and the run asks for it instead.
    /// </summary>
    public static string Render(IEnumerable<(string Name, string? Value, QueryParameterKind Kind)> values)
    {
        var sb = new StringBuilder();
        foreach (var (name, value, kind) in values)
            if (FormatValue(value, kind) is { } written)
                sb.Append("@set ").Append(name).Append(" = ").Append(written).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="sql"/> with every directive line replaced by spaces — same length, line breaks kept, so
    /// every offset in it still points where it did (statement ends, inline result placement, error positions).
    /// </summary>
    public static string Blank(string sql, IReadOnlyList<ScriptDirective> directives)
    {
        if (directives.Count == 0) return sql;
        var chars = sql.ToCharArray();
        foreach (var d in directives)
            for (var i = d.Start; i < d.End && i < chars.Length; i++)
                chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>The Postgres reading: a directive's <c>@</c> must start a token the vendored lexer puts on the
    /// default channel — not inside a string, a dollar-quoted body or a comment (hidden channel).</summary>
    internal static IReadOnlyList<ScriptDirective> FindPostgres(string sql)
        => Lines(sql, PgParsing.LexAll(sql)
            .Where(t => t.Type != TokenConstants.EOF && t.Channel == TokenConstants.DefaultChannel)
            .Select(t => t.StartIndex));

    /// <summary>The T-SQL reading, over <see cref="TSqlScanner"/> (which drops comments).</summary>
    internal static IReadOnlyList<ScriptDirective> FindTSql(string sql)
        => Lines(sql, TSqlScanner.Tokenize(sql).Select(t => t.Start));

    /// <summary>The MySQL / MariaDB reading, over <see cref="MySqlScanner"/> (which drops comments).</summary>
    internal static IReadOnlyList<ScriptDirective> FindMySql(string sql)
        => Lines(sql, MySqlScanner.Tokenize(sql).Select(t => t.Start));

    /// <summary>The lines shaped like a directive whose <c>@</c> starts a real token.</summary>
    private static IReadOnlyList<ScriptDirective> Lines(string sql, IEnumerable<int> tokenStarts)
    {
        var starts = tokenStarts.ToHashSet();
        var found = new List<ScriptDirective>();
        foreach (Match m in LinePattern().Matches(sql))
        {
            var at = sql.IndexOf('@', m.Index);
            if (!starts.Contains(at)) continue;
            var (value, kind) = ParseValue(m.Groups[2].Value);
            found.Add(new ScriptDirective(m.Groups[1].Value, value, kind, m.Index, m.Length, IsGiven(m.Groups[2].Value)));
        }
        return found;
    }

    /// <summary>The text inside a whole <c>'…'</c> with <c>''</c> for a quote, or null when the quotes don't
    /// enclose the whole value (<c>'a' 'b'</c>, <c>'it's'</c>) — that is taken as written instead.</summary>
    private static string? Unquote(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 1; i < text.Length - 1; i++)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length - 1 && text[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                return null;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }
}
