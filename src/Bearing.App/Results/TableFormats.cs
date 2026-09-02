using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bearing.App.Formatting;

namespace Bearing.App.Results;

/// <summary>
/// The text renderings a <see cref="TableBlock"/> can be copied or exported as. Pure: string in, string out,
/// no clipboard and no files (see <c>ResultExport</c> for writing one to disk).
/// <para>
/// All of them share <see cref="Text"/>, which is the grid's own cell text (so a copied date matches the
/// displayed date — both ISO, see <see cref="CellFormat"/>) with one exception: bytea is emitted whole.
/// The grid truncates it to keep a column narrow; a clipboard or a file has no such excuse, and a silently
/// shortened value is worse than a long one.
/// </para>
/// </summary>
public static class TableFormats
{
    /// <summary>A cell's text, or null for a SQL NULL (each format decides how to spell that).</summary>
    public static string? Text(object? value) => value switch
    {
        null => null,
        byte[] bytes => @"\x" + Convert.ToHexString(bytes).ToLowerInvariant(), // whole, never truncated
        _ => CellFormat.Display(value),
    };

    // ---- CSV -------------------------------------------------------------------------------------

    /// <summary>
    /// RFC 4180 CSV with a header row: CRLF line endings (what the RFC says and what Excel expects on every
    /// platform), fields quoted only when they need it, embedded quotes doubled. A NULL is an <i>empty
    /// unquoted</i> field and an empty string is <c>""</c> — the same distinction Postgres's own
    /// <c>COPY … CSV</c> makes, so the two don't become indistinguishable on the way out.
    /// </summary>
    public static string Csv(TableBlock block)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", block.Columns.Select(c => CsvField(c.Name)))).Append("\r\n");
        for (var r = 0; r < block.Rows.Count; r++)
        {
            for (var c = 0; c < block.Columns.Count; c++)
            {
                if (c > 0) sb.Append(',');
                if (Text(block.Value(r, c)) is { } text) sb.Append(CsvField(text));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string CsvField(string value)
        => value.Length == 0
            || value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ---- Markdown --------------------------------------------------------------------------------

    /// <summary>A GitHub-flavoured Markdown table. Cell text can't contain a line break in this syntax, so
    /// newlines become <c>&lt;br&gt;</c> and pipes are escaped; a NULL renders as an empty cell.</summary>
    public static string Markdown(TableBlock block)
    {
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", block.Columns.Select(c => MarkdownCell(c.Name)))).Append(" |\n");
        sb.Append('|').Append(string.Concat(block.Columns.Select(_ => " --- |"))).Append('\n');
        for (var r = 0; r < block.Rows.Count; r++)
        {
            sb.Append("| ");
            for (var c = 0; c < block.Columns.Count; c++)
            {
                if (c > 0) sb.Append(" | ");
                sb.Append(MarkdownCell(Text(block.Value(r, c)) ?? ""));
            }
            sb.Append(" |\n");
        }
        return sb.ToString();
    }

    private static string MarkdownCell(string value)
        => value.Replace("\\", "\\\\").Replace("|", "\\|")
                .Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    // ---- JSON ------------------------------------------------------------------------------------

    /// <summary>
    /// An array of objects, one per row, keyed by column name — the shape an API or a script expects.
    /// Values keep their type: numbers and booleans are bare, a NULL is <c>null</c>, dates are their ISO
    /// text, and a Postgres array becomes a JSON array. Duplicate column names (<c>select a.id, b.id</c>)
    /// are suffixed <c>_2</c>, <c>_3</c> so no key is silently dropped by a consumer.
    /// </summary>
    public static string Json(TableBlock block)
    {
        var names = UniqueNames(block);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            // The relaxed encoder, or System.Text.Json's default escapes anything that could be unsafe in
            // an HTML document — which turned the "+" of a timestamp's offset into "\u002B", so a copied
            // timestamptz read 2026-07-16 14:16:49\u002B00:00. It does the same to non-ASCII, so a name
            // with an accent came out as a run of escapes too. This is clipboard text, not markup: the only
            // consumer is whatever the user pastes into, and an escape there is noise they have to undo.
            //
            // Safe because the HTML flavour is a different method with its own HtmlEscape — nothing built
            // here is ever interpolated into a document by us. Same encoder JsonText already uses for the
            // cell inspector, so the inspector and the clipboard agree about the same value.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartArray();
            for (var r = 0; r < block.Rows.Count; r++)
            {
                w.WriteStartObject();
                for (var c = 0; c < block.Columns.Count; c++)
                {
                    w.WritePropertyName(names[c]);
                    WriteJsonValue(w, block.Value(r, c));
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case byte or sbyte or short or ushort or int: w.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
            case uint or long: w.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong u: w.WriteNumberValue(u); break;
            case decimal m: w.WriteNumberValue(m); break;
            case float f: w.WriteNumberValue(f); break;
            case double d: w.WriteNumberValue(d); break;
            // byte[] is an Array, so it has to be matched before the array arm: bytea is a hex string,
            // not a list of 200 numbers.
            case byte[] bytes: w.WriteStringValue(Text(bytes)); break;
            case Array arr:
                w.WriteStartArray();
                foreach (var item in arr) WriteJsonValue(w, item);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Text(value)); break;
        }
    }

    // ---- HTML ------------------------------------------------------------------------------------

    /// <summary>
    /// A <c>&lt;table&gt;</c> fragment for pasting into Teams, Outlook, Word or Excel — which is why it is
    /// styled and why the styling is <b>inline</b>: those editors run the pasted markup through a sanitiser
    /// that drops <c>&lt;style&gt;</c> blocks and classes, so a rule that isn't on the element itself is a
    /// rule that won't survive. Borders are also given as the legacy <c>border</c>/<c>cellspacing</c>
    /// attributes, which some sanitisers keep after stripping CSS. A NULL is an empty cell.
    /// <para>
    /// This is the <i>markup</i> only. Getting a table rather than a wall of angle brackets depends on it
    /// reaching the clipboard as the platform's HTML flavour — see <c>Services/HtmlClipboard</c>.
    /// </para>
    /// </summary>
    public static string Html(TableBlock block)
    {
        const string cell = "border:1px solid #d0d0d0;padding:4px 8px;";
        var sb = new StringBuilder();
        sb.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" ")
          .Append("style=\"border-collapse:collapse;font-family:sans-serif;font-size:10pt;\">\n");
        sb.Append("  <thead>\n    <tr>");
        foreach (var col in block.Columns)
            sb.Append("<th style=\"").Append(cell).Append("text-align:left;background:#f2f2f2;\">")
              .Append(HtmlEscape(col.Name)).Append("</th>");
        sb.Append("</tr>\n  </thead>\n  <tbody>\n");
        for (var r = 0; r < block.Rows.Count; r++)
        {
            sb.Append("    <tr>");
            for (var c = 0; c < block.Columns.Count; c++)
                sb.Append("<td style=\"").Append(cell).Append("\">")
                  .Append(HtmlEscape(Text(block.Value(r, c)) ?? "")).Append("</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("  </tbody>\n</table>");
        return sb.ToString();
    }

    private static string HtmlEscape(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ---- SQL -------------------------------------------------------------------------------------

    /// <summary>
    /// One <c>insert into … values (…);</c> per row, values rendered as SQL literals
    /// (<see cref="SqlValue"/>, so dates are ISO rather than culture-dependent). Identifiers are quoted, so
    /// a mixed-case or reserved column name survives. Display/transfer text only — the app's own writes stay
    /// parameterized (§5.4).
    /// </summary>
    public static string SqlInsert(TableBlock block, string? schema, string table)
    {
        var target = schema is { Length: > 0 } ? $"{Quote(schema)}.{Quote(table)}" : Quote(table);
        var columns = string.Join(", ", block.Columns.Select(c => Quote(c.Name)));
        var sb = new StringBuilder();
        for (var r = 0; r < block.Rows.Count; r++)
        {
            var values = new List<string>(block.Columns.Count);
            for (var c = 0; c < block.Columns.Count; c++) values.Add(SqlValue.Literal(block.Value(r, c)));
            sb.Append("insert into ").Append(target).Append(" (").Append(columns)
              .Append(")\nvalues (").Append(string.Join(", ", values)).Append(");\n");
        }
        return sb.ToString();
    }

    private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Every selected value as a comma-separated list of SQL literals, ready to paste <i>between the
    /// parentheses</i> of <c>where id in (…)</c> — so no parentheses of its own, and one line.
    /// <para>
    /// Two deliberate filters. <b>Duplicates collapse</b> (first occurrence wins, order kept), because the
    /// normal way to get this list is to select a column that repeats. <b>NULLs are dropped</b>, because a
    /// NULL in an <c>in (…)</c> list matches nothing while looking like it might — and in a <c>not in (…)</c>
    /// list it is worse than useless: the comparison goes unknown for every row, so the query silently
    /// returns nothing at all.
    /// </para>
    /// </summary>
    public static string InList(TableBlock block)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>();
        for (var r = 0; r < block.Rows.Count; r++)
            for (var c = 0; c < block.Columns.Count; c++)
            {
                if (block.Value(r, c) is not { } value) continue;   // NULL → skipped, see above
                var literal = SqlValue.Literal(value);
                if (seen.Add(literal)) values.Add(literal);
            }
        return string.Join(", ", values);
    }

    // ---- shared ----------------------------------------------------------------------------------

    /// <summary>Column names made unique (<c>id</c>, <c>id_2</c>, …) for formats where a name is a key.</summary>
    internal static string[] UniqueNames(TableBlock block)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new string[block.Columns.Count];
        for (var i = 0; i < names.Length; i++)
        {
            var name = block.Columns[i].Name;
            if (seen.TryGetValue(name, out var count))
            {
                seen[name] = ++count;
                names[i] = $"{name}_{count}";
            }
            else
            {
                seen[name] = 1;
                names[i] = name;
            }
        }
        return names;
    }
}
