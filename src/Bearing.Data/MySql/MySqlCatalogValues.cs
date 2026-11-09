using System.Globalization;
using System.Text;

namespace Bearing.Data.MySql;

/// <summary>
/// Catalog values the two engines spell differently, brought to one reading so the explorer shows the same
/// thing for the same table on either server. Pure, so each rule is testable without a server (§2.5).
/// </summary>
internal static class MySqlCatalogValues
{
    /// <summary>
    /// <c>information_schema.columns.column_default</c> as the default a person would write. MySQL reports a
    /// literal bare (<c>none</c>) and a column with no default as SQL NULL. MariaDB (10.2.7+) reports a
    /// literal quoted (<c>'none'</c>, with <c>''</c> for an embedded quote) and a column with no default — or
    /// an explicit <c>DEFAULT NULL</c>, which means the same — as the text <c>NULL</c>. A default that is the
    /// string "NULL" is quoted there, so the two stay distinct. Expressions (<c>current_timestamp()</c>) and
    /// numbers are bare on both and pass through.
    /// </summary>
    public static string? ColumnDefault(string? raw, bool isMariaDb)
    {
        if (raw is null || !isMariaDb)
        {
            return raw;
        }
        if (raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return Unescape(raw[1..^1]);
        }
        return raw;
    }

    /// <summary>
    /// The body of MariaDB's quoted default back to the value: <c>''</c> is a quote, and a backslash escapes
    /// the next character the way a MySQL string does — MariaDB reports <c>a\b</c> as <c>'a\\b'</c> and a
    /// newline as <c>\n</c> (measured on 11.4).
    /// </summary>
    private static string Unescape(string body)
    {
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                i++;
                sb.Append(body[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    'b' => '\b',
                    'Z' => '\u001a',
                    var other => other,
                });
                continue;
            }
            if (c == '\'' && i + 1 < body.Length && body[i + 1] == '\'')
            {
                sb.Append('\'');
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A sequence setting as a <see cref="long"/>, clamped when it does not fit — an unsigned MariaDB sequence
    /// (11.5+) runs to 18446744073709551614 — with <see cref="Bound.Clamped"/> saying so, rather than the
    /// read throwing and taking every other sequence in the database with it.
    /// </summary>
    public static Bound SequenceBound(object value)
    {
        var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (d > long.MaxValue)
        {
            return new Bound(long.MaxValue, Clamped: true);
        }
        if (d < long.MinValue)
        {
            return new Bound(long.MinValue, Clamped: true);
        }
        return new Bound((long)d, Clamped: false);
    }

    internal readonly record struct Bound(long Value, bool Clamped);
}
