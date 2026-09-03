using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Bearing.App.Results;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Copy as ▸ and Export share one pipeline: a <see cref="TableBlock"/> (from a selection or a whole result)
/// rendered by <see cref="TableFormats"/>. Both halves are pure, which is the only way any of this is
/// verifiable — the grid itself can't be driven headlessly (§4.3).
/// </summary>
public class TableFormatsTests
{
    private static ResultSetViewModel Result(
        (string Name, string Type, Type Clr)[] columns, params object?[][] rows)
    {
        var descriptors = columns.Select(c => new ColumnDescriptor(c.Name, c.Type, c.Clr)).ToArray();
        var result = new QueryResult(descriptors, rows, rows.Length, TimeSpan.Zero, null, null, false);
        return new ResultSetViewModel(result, "select * from t", pageable: false);
    }

    /// <summary>(id int, name text, price numeric, at timestamp) — one plain, typed result.</summary>
    private static ResultSetViewModel Sample() => Result(
        [("id", "int4", typeof(int)), ("name", "text", typeof(string)),
         ("price", "numeric", typeof(decimal)), ("at", "timestamp", typeof(DateTime))],
        [1, "widget", 9.5m, new DateTime(2026, 8, 11, 14, 3, 22)],
        [2, null, -0.25m, null]);

    // ---- cell text ---------------------------------------------------------------------------

    [Fact]
    public void Cell_text_matches_the_grid_but_never_truncates_bytea()
    {
        // The grid caps a bytea at 16 bytes to keep the column narrow; a file or clipboard has no such
        // excuse, and a silently shortened value is worse than a long one.
        var twenty = new byte[20];
        twenty[19] = 0xFF;
        var text = TableFormats.Text(twenty)!;
        Assert.DoesNotContain("…", text);
        Assert.DoesNotContain("bytes", text);
        Assert.Equal(2 + 40, text.Length);                    // \x + 20 bytes of hex
        Assert.EndsWith("ff", text);

        Assert.Null(TableFormats.Text(null));                 // NULL is the absence of text, not "(null)"
        Assert.Equal("2026-08-11 14:03:22", TableFormats.Text(new DateTime(2026, 8, 11, 14, 3, 22)));
    }

    // ---- blocks ------------------------------------------------------------------------------

    [Fact]
    public void A_selection_block_is_the_bounding_rectangle_with_gaps_filled()
    {
        var rs = Sample();
        // Ctrl-clicked diagonal: (row0,col0) and (row1,col1) only. TSV blanks the two gaps to keep a
        // spreadsheet paste aligned; the structured formats need real data, so the rectangle is filled.
        var cells = new List<(object?[] Row, int Col)> { (rs.Rows[0], 0), (rs.Rows[1], 1) };
        var block = TableBlock.ForSelection(rs, cells);

        Assert.Equal(new[] { "id", "name" }, block.Columns.Select(c => c.Name));
        Assert.Equal(2, block.Rows.Count);
        Assert.Equal("widget", block.Value(0, 1));            // the gap carries its real value
        Assert.Equal(2, block.Value(1, 0));
        // …and TSV is unchanged, still shape-preserving: the two unselected slots stay blank, while the
        // selected NULL still spells itself out.
        Assert.Equal("1\t\n\t(null)", GridSelectionOps.Tsv(rs, cells));
    }

    [Fact]
    public void An_empty_or_stranded_selection_makes_an_empty_block()
    {
        var rs = Sample();
        Assert.True(TableBlock.ForSelection(rs, Array.Empty<(object?[], int)>()).IsEmpty);
        var dropped = new object?[] { 99, "gone", 0m, null }; // a discarded pending-new row
        Assert.True(TableBlock.ForSelection(rs, new[] { (dropped, 0) }).IsEmpty);
    }

    [Fact]
    public void A_result_block_is_every_loaded_row_and_column()
    {
        var block = TableBlock.ForResult(Sample());
        Assert.Equal(4, block.Columns.Count);
        Assert.Equal(2, block.Rows.Count);
    }

    // ---- CSV ---------------------------------------------------------------------------------

    [Fact]
    public void Csv_has_a_header_crlf_lines_and_iso_dates()
    {
        var csv = TableFormats.Csv(TableBlock.ForResult(Sample()));
        var lines = csv.Split("\r\n");
        Assert.Equal("id,name,price,at", lines[0]);
        Assert.Equal("1,widget,9.5,2026-08-11 14:03:22", lines[1]);
        Assert.Equal("", lines[^1]);                 // trailing terminator, not a blank record
        Assert.DoesNotContain("\n\n", csv);
    }

    [Fact]
    public void Csv_distinguishes_null_from_an_empty_string()
    {
        // Postgres's own COPY … CSV makes exactly this distinction: NULL is an empty *unquoted* field.
        var rs = Result([("a", "text", typeof(string)), ("b", "text", typeof(string))], [null, ""]);
        var row = TableFormats.Csv(TableBlock.ForResult(rs)).Split("\r\n")[1];
        Assert.Equal(",\"\"", row);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with,comma", "\"with,comma\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(" padded ", "\" padded \"")]      // quoted, or a reader is free to trim it away
    public void Csv_quotes_only_what_needs_quoting(string value, string expected)
    {
        var rs = Result([("a", "text", typeof(string))], [value]);
        Assert.Equal(expected, TableFormats.Csv(TableBlock.ForResult(rs)).Split("\r\n")[1]);
    }

    // ---- Markdown ----------------------------------------------------------------------------

    [Fact]
    public void Markdown_is_a_table_with_escaped_pipes_and_no_raw_newlines()
    {
        var rs = Result([("a", "text", typeof(string))], ["x|y"], ["two\nlines"]);
        var md = TableFormats.Markdown(TableBlock.ForResult(rs));
        var lines = md.TrimEnd('\n').Split('\n');

        Assert.Equal("| a |", lines[0]);
        Assert.Equal("| --- |", lines[1]);
        Assert.Equal(@"| x\|y |", lines[2]);        // an unescaped pipe would split the cell
        Assert.Equal("| two<br>lines |", lines[3]); // the syntax has no way to hold a line break
        Assert.Equal(4, lines.Length);
    }

    // ---- JSON --------------------------------------------------------------------------------

    [Fact]
    public void Json_keeps_types_rather_than_stringifying_everything()
    {
        var json = TableFormats.Json(TableBlock.ForResult(Sample()));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var first = doc.RootElement[0];

        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal(1, first.GetProperty("id").GetInt32());                    // number, not "1"
        Assert.Equal(9.5m, first.GetProperty("price").GetDecimal());
        Assert.Equal("2026-08-11 14:03:22", first.GetProperty("at").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement[1].GetProperty("name").ValueKind);
    }

    [Fact]
    public void Json_arrays_stay_arrays_and_bytea_stays_a_hex_string()
    {
        var rs = Result([("tags", "text[]", typeof(string[])), ("blob", "bytea", typeof(byte[]))],
            [new[] { "a", "b" }, new byte[] { 0xDE, 0xAD }]);
        using var doc = System.Text.Json.JsonDocument.Parse(TableFormats.Json(TableBlock.ForResult(rs)));
        var row = doc.RootElement[0];

        Assert.Equal(new[] { "a", "b" }, row.GetProperty("tags").EnumerateArray().Select(e => e.GetString()));
        // byte[] is an Array too — matching it first is what stops a bytea becoming a list of numbers.
        Assert.Equal(@"\xdead", row.GetProperty("blob").GetString());
    }

    [Fact]
    public void Json_disambiguates_duplicate_column_names()
    {
        // `select a.id, b.id from …` — a consumer keyed by name would otherwise lose one silently.
        var rs = Result([("id", "int4", typeof(int)), ("id", "int4", typeof(int))], [1, 2]);
        using var doc = System.Text.Json.JsonDocument.Parse(TableFormats.Json(TableBlock.ForResult(rs)));
        Assert.Equal(1, doc.RootElement[0].GetProperty("id").GetInt32());
        Assert.Equal(2, doc.RootElement[0].GetProperty("id_2").GetInt32());
    }

    // ---- HTML (the "paste a table into Teams" format) ----------------------------------------

    [Fact]
    public void Html_is_an_escaped_table_with_inline_styling()
    {
        var rs = Result([("a", "text", typeof(string))], ["<b>&</b>"]);
        var html = TableFormats.Html(TableBlock.ForResult(rs));

        Assert.StartsWith("<table ", html);
        Assert.EndsWith("</table>", html);
        Assert.Contains("&lt;b&gt;&amp;&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>", html);            // never re-emitted as live markup
        // Inline, not a <style> block or classes: Teams/Outlook/Word run pasted markup through a sanitiser
        // that drops both, so anything not on the element itself is lost.
        Assert.Contains("<td style=\"border:1px solid", html);
        Assert.Contains("border-collapse:collapse", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("class=", html);
        // Legacy attributes too — some sanitisers keep these after stripping the CSS.
        Assert.Contains("border=\"1\"", html);
        Assert.Contains("cellspacing=\"0\"", html);
        // Plain Html carries no query row: only the with-query format volunteers the statement.
        Assert.DoesNotContain("<pre", html);
        Assert.DoesNotContain("colspan", html);
    }

    /// <summary>
    /// The with-query table puts the statement in one full-width row above the headers, so a table pasted
    /// into a chat or a report says where its numbers came from (what DBeaver's Copy as HTML does).
    /// </summary>
    [Fact]
    public void Html_can_carry_the_query_that_produced_the_rows()
    {
        var rs = Sample();
        var block = TableBlock.ForResult(rs);

        var html = TableFormats.Html(block, "select id, name\nfrom t\nwhere name <> 'a & b'");

        // One cell spanning every column, above the header row.
        Assert.Contains("<th colspan=\"4\"", html);
        Assert.True(html.IndexOf("colspan", StringComparison.Ordinal)
                  < html.IndexOf(">price<", StringComparison.Ordinal), "query row must precede the headers");
        Assert.Equal(1, html.Split("colspan").Length - 1);       // one query row, not one per column
        // Monospaced and escaped like any other cell — the statement is text here, not markup.
        Assert.Contains("<pre style=\"margin:0;font-family:monospace", html);
        Assert.Contains("where name &lt;&gt; 'a &amp; b'", html);
        // Line breaks as <br>, not as newlines inside the <pre>: the white-space rule that would render
        // those is CSS, which is exactly what the Teams / Outlook / Word sanitisers drop.
        Assert.Contains("select id, name<br>from t<br>where", html);
    }

    [Fact]
    public void The_query_row_is_omitted_rather_than_left_empty_when_there_is_no_query()
    {
        var block = TableBlock.ForResult(Sample());

        foreach (var nothing in new[] { null, "", "   \n\t " })
            Assert.DoesNotContain("colspan", TableFormats.Html(block, nothing));

        // And the surrounding whitespace of a real statement doesn't reach the clipboard either — the
        // statement under the caret arrives with the newlines that separated it from its neighbours.
        Assert.Contains("<pre style=\"margin:0;font-family:monospace;font-size:9pt;white-space:pre-wrap;\">select 1</pre>",
            TableFormats.Html(block, "\n  select 1  \n\n"));
    }

    /// <summary>
    /// The statement travels on the result set, not on the copy call, so it is available for every result —
    /// a write or one set of a batch included, where <see cref="ResultSetViewModel.SourceSql"/> is null
    /// because there is nothing to page.
    /// </summary>
    [Fact]
    public void The_executed_statement_is_kept_even_where_paging_is_impossible()
    {
        var rs = Result([("id", "int4", typeof(int))], [1]);       // pageable: false — see the helper

        Assert.Null(rs.SourceSql);
        Assert.Equal("select * from t", rs.ExecutedSql);
        Assert.Contains("select * from t", CopyRenderer.Render(rs, TableBlock.ForResult(rs), CopyFormat.HtmlWithQuery));
        Assert.DoesNotContain("select * from t", CopyRenderer.Render(rs, TableBlock.ForResult(rs), CopyFormat.Html));
    }

    /// <summary>
    /// Both table formats ride the platform's HTML flavour, and the plain-text alternative that goes with
    /// them keeps the query when the user asked for it — losing the half they chose the format for would be
    /// the worse failure of the two.
    /// </summary>
    [Fact]
    public void The_rich_formats_and_their_plain_text_alternative_agree_about_the_query()
    {
        var rs = Sample();

        Assert.True(CopyRenderer.IsRichHtml(CopyFormat.Html));
        Assert.True(CopyRenderer.IsRichHtml(CopyFormat.HtmlWithQuery));
        Assert.False(CopyRenderer.IsRichHtml(CopyFormat.Csv));

        Assert.Equal("1\t2", CopyRenderer.PlainAlternative(rs, "1\t2", CopyFormat.Html));
        Assert.Equal("select * from t\n\n1\t2", CopyRenderer.PlainAlternative(rs, "1\t2", CopyFormat.HtmlWithQuery));
    }

    [Fact]
    public void The_html_clipboard_document_wraps_the_fragment_once()
    {
        var doc = HtmlClipboard.Document("<table></table>");
        Assert.StartsWith("<html>", doc);
        Assert.EndsWith("</html>", doc);
        Assert.Contains("charset=\"utf-8\"", doc);   // or a non-ASCII value pastes mangled
        Assert.Contains("<body><table></table></body>", doc);
    }

    [Fact]
    public void Each_platform_publishes_html_under_its_own_flavour_name_as_bytes()
    {
        // Bytes, not strings, on every platform. Measured on X11/XWayland with Avalonia 12.1: a *string*
        // platform format is advertised on the clipboard but serves an empty payload, so the target asks for
        // HTML, gets nothing, and pastes the plain text instead — which is precisely the bug this fixes.
        var (mime, mimeBytes) = HtmlClipboard.Payload("<table/>", HtmlClipboard.RichHtmlTarget.MimeHtml);
        Assert.Equal("text/html", mime);
        Assert.Equal(HtmlClipboard.Document("<table/>"), Encoding.UTF8.GetString(mimeBytes));

        var (apple, appleBytes) = HtmlClipboard.Payload("<table/>", HtmlClipboard.RichHtmlTarget.AppleHtml);
        Assert.Equal("public.html", apple);        // a UTI, not a mime type
        Assert.Equal(mimeBytes, appleBytes);

        // Windows takes CF_HTML — the offset header, not bare markup — and that branch is unreachable by hand
        // on this machine, which is why it is asserted here.
        var (windows, windowsBytes) = HtmlClipboard.Payload("<table/>", HtmlClipboard.RichHtmlTarget.WindowsCfHtml);
        Assert.Equal("HTML Format", windows);
        Assert.StartsWith("Version:0.9", Encoding.UTF8.GetString(windowsBytes));
    }

    [Fact]
    public void The_windows_cf_html_header_offsets_match_the_payload_bytes()
    {
        // CF_HTML's four numbers are byte offsets into the payload that contains them — get one wrong and
        // Windows pastes nothing at all, silently. Unverifiable on this machine, so it is pinned here.
        var payload = CfHtml.Wrap("<table><tr><td>é</td></tr></table>"); // non-ASCII: bytes ≠ chars
        var text = Encoding.UTF8.GetString(payload);

        var startHtml = Header(text, "StartHTML");
        var endHtml = Header(text, "EndHTML");
        var startFragment = Header(text, "StartFragment");
        var endFragment = Header(text, "EndFragment");

        Assert.Contains("Version:0.9", text);
        Assert.Equal(payload.Length, endHtml);                       // EndHTML = end of the whole payload
        Assert.Equal("<html>", Slice(payload, startHtml, startHtml + 6));
        // The fragment offsets bracket exactly the markup between the two markers …
        Assert.Equal("<table><tr><td>é</td></tr></table>", Slice(payload, startFragment, endFragment));
        // … and they are *byte* offsets: the é is two bytes, so EndFragment sits one past the char index of
        // the marker it points at. Computing these over char indices is the mistake this pins.
        Assert.Equal(text.IndexOf("<!--EndFragment-->", StringComparison.Ordinal) + 1, endFragment);
        Assert.True(startHtml < startFragment && startFragment < endFragment && endFragment < endHtml);
    }

    /// <summary>The 10-digit, zero-padded value of a CF_HTML header field.</summary>
    private static int Header(string payload, string field)
    {
        var match = System.Text.RegularExpressions.Regex.Match(payload, $@"(?m)^{field}:(\d{{10}})\r$");
        Assert.True(match.Success, $"{field} missing or not 10-digit padded");
        return int.Parse(match.Groups[1].Value);
    }

    private static string Slice(byte[] payload, int start, int end)
        => Encoding.UTF8.GetString(payload, start, end - start);

    // ---- IN list -----------------------------------------------------------------------------

    [Fact]
    public void An_in_list_is_comma_separated_literals_with_no_parentheses()
    {
        // Pasted *between* the parens of `where id in (…)`, so supplying its own would double them.
        var rs = Result([("id", "int4", typeof(int))], [1], [2], [3]);
        Assert.Equal("1, 2, 3", TableFormats.InList(TableBlock.ForResult(rs)));
    }

    [Fact]
    public void An_in_list_quotes_text_and_dates_the_way_sql_needs()
    {
        var rs = Result(
            [("name", "text", typeof(string)), ("at", "timestamp", typeof(DateTime))],
            ["O'Brien", new DateTime(2026, 8, 11, 14, 3, 22)]);

        Assert.Equal("'O''Brien', '2026-08-11 14:03:22'", TableFormats.InList(TableBlock.ForResult(rs)));
    }

    [Fact]
    public void An_in_list_collapses_duplicates_and_drops_nulls()
    {
        // Selecting a repeating column is the normal way to build one of these, so duplicates collapse
        // (first occurrence wins). NULL is dropped because `in (null)` matches nothing while looking like it
        // might — and in a `not in (…)` list it makes the predicate unknown for every row, silently
        // returning no rows at all.
        var rs = Result([("id", "int4", typeof(int))], [7], [7], [null], [8], [7]);
        Assert.Equal("7, 8", TableFormats.InList(TableBlock.ForResult(rs)));

        // `new object?[] { null }`, not `[null]`: as the sole argument to a `params object?[][]`, the
        // collection expression binds as the params array itself — one *null row* rather than one null cell.
        var allNull = Result([("id", "int4", typeof(int))], new object?[] { null });
        Assert.Equal("", TableFormats.InList(TableBlock.ForResult(allNull)));
    }

    [Fact]
    public void An_in_list_of_a_multi_column_selection_takes_every_value_in_reading_order()
    {
        var rs = Result([("a", "int4", typeof(int)), ("b", "int4", typeof(int))], [1, 2], [3, 4]);
        Assert.Equal("1, 2, 3, 4", TableFormats.InList(TableBlock.ForResult(rs)));
    }

    // ---- SQL ---------------------------------------------------------------------------------

    [Fact]
    public void Sql_insert_quotes_identifiers_and_renders_typed_literals()
    {
        var rs = Sample();
        var sql = TableFormats.SqlInsert(TableBlock.ForResult(rs), "public", "orders");
        var statements = sql.TrimEnd('\n').Split(";\n");

        Assert.Equal(2, statements.Length);
        Assert.Contains("insert into \"public\".\"orders\" (\"id\", \"name\", \"price\", \"at\")", statements[0]);
        Assert.Contains("values (1, 'widget', 9.5, '2026-08-11 14:03:22')", statements[0]);
        Assert.Contains("values (2, null, -0.25, null)", statements[1]); // NULL is the keyword, not 'null'
    }

    [Fact]
    public void Sql_insert_escapes_quotes_and_names_no_table_it_cannot_know()
    {
        var rs = Result([("name", "text", typeof(string))], ["O'Brien"]);
        // A join / view / expression select has no single table, so the placeholder is deliberately
        // conspicuous rather than a plausible-looking guess.
        var sql = CopyRenderer.Render(rs, TableBlock.ForResult(rs), CopyFormat.SqlInsert);
        Assert.Contains(CopyRenderer.UnknownTable, sql);
        Assert.Contains("'O''Brien'", sql);
    }

    [Fact]
    public void Sql_insert_uses_the_edit_target_when_the_result_has_one()
    {
        var rs = Result([("id", "int4", typeof(int))], [7]);
        var editable = new ResultSetViewModel(
            new QueryResult(rs.Columns, rs.Rows.ToList(), 1, TimeSpan.Zero, null, null, false),
            "select id from public.orders", pageable: false)
        {
            EditTarget = new EditTarget("public", "orders", [new EditableColumn(0, "id", true)]),
        };

        Assert.Contains("\"public\".\"orders\"",
            CopyRenderer.Render(editable, TableBlock.ForResult(editable), CopyFormat.SqlInsert));
    }

    // ---- dispatch ----------------------------------------------------------------------------

    [Fact]
    public void Every_alternative_renders_and_is_offered_in_the_menu()
    {
        var rs = Sample();
        var block = TableBlock.ForResult(rs);
        foreach (var format in CopyRenderer.Alternatives)
        {
            var text = CopyRenderer.Render(rs, block, format);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.False(string.IsNullOrWhiteSpace(CopyRenderer.Label(format)));
            // Unlike plain Copy (TSV), the tabular formats name their columns. The IN list is the deliberate
            // exception: it carries values only, because it is pasted into an existing predicate.
            if (format != CopyFormat.InList) Assert.Contains("price", text);
        }

        // A new format must reach the menu and the command table, both of which read Alternatives — so a
        // member added to the enum and forgotten here fails the build's test run.
        Assert.Equal(Enum.GetValues<CopyFormat>().Length - 1, CopyRenderer.Alternatives.Count); // -1: Tsv is plain Copy
        Assert.DoesNotContain(CopyFormat.Tsv, CopyRenderer.Alternatives);
    }

    // ---- escaping ---------------------------------------------------------------------------------

    /// <summary>
    /// A timestamp's offset keeps its <c>+</c>, and an accented name keeps its letters.
    /// <para>
    /// System.Text.Json's default encoder escapes anything that could be unsafe inside an HTML document, so
    /// a copied <c>timestamptz</c> arrived as <c>2026-07-16 14:16:49\u002B00:00</c> and an accented name as a
    /// run of <c>\uXXXX</c>. Reported from a real copy. This is clipboard text: the consumer is whatever the
    /// user pastes into, and an escape there is noise they have to undo by hand.
    /// </para>
    /// </summary>
    [Fact]
    public void Json_leaves_a_plus_and_an_accent_alone()
    {
        var result = Result(
            [("created_time", "text", typeof(string)), ("name", "text", typeof(string))],
            ["2026-07-16 14:16:49.688644+00:00", "Čevapčići"]);

        var json = TableFormats.Json(TableBlock.ForResult(result));

        Assert.Contains("+00:00", json);
        Assert.DoesNotContain("u002B", json);
        Assert.Contains("Čevapčići", json);
    }

    /// <summary>
    /// …and everything JSON itself requires is still escaped.
    /// <para>
    /// The relaxed encoder relaxes HTML-safety, not JSON validity. A raw quote or backslash would produce a
    /// document no parser accepts — a far worse bug than a stray escape — so the real assertion is that it
    /// round-trips through a parser.
    /// </para>
    /// </summary>
    [Fact]
    public void Json_still_escapes_what_json_requires()
    {
        const string awkward = "she said QUOTEhiQUOTE BSLASH then leftNEWLINEnext";
        var value = awkward.Replace("QUOTE", "STRQ").Replace("BSLASH", "STRB").Replace("NEWLINE", "STRN")
            .Replace("STRQ", "\"").Replace("STRB", "\\").Replace("STRN", "\n");

        var result = Result([("note", "text", typeof(string))], [value]);

        var json = TableFormats.Json(TableBlock.ForResult(result));

        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(value, parsed.RootElement[0].GetProperty("note").GetString());
    }

    /// <summary>
    /// The HTML flavour keeps escaping markup, because that one really is markup.
    /// <para>
    /// Relaxing the JSON encoder is only safe while this holds: the two formats are separate methods with
    /// separate escaping, and the HTML one is what travels to Teams and Outlook.
    /// </para>
    /// </summary>
    [Fact]
    public void Html_still_escapes_markup()
    {
        var result = Result([("note", "text", typeof(string))], ["<script>alert(1)</script> & co"]);

        var html = TableFormats.Html(TableBlock.ForResult(result));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
    }
}
