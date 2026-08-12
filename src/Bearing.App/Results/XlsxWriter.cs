using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Bearing.App.Results;

/// <summary>
/// Writes a <see cref="TableBlock"/> as a minimal <c>.xlsx</c> workbook, by hand.
/// <para>
/// <b>Why no library.</b> An xlsx is a zip of a handful of XML parts, and everything this app needs from one
/// is a single sheet: a bold frozen header, typed cells, and dates that arrive as dates rather than as text.
/// That is the ~150 lines below, against a multi-megabyte dependency (ClosedXML/NPOI/OpenXml SDK) whose
/// styling, formula and pivot machinery would go unused. It also matches how the rest of the repo is built —
/// raw ADO instead of an ORM, hand-rolled fakes instead of a mocking framework.
/// </para>
/// <para>
/// <b>Verifying a change here.</b> The unit tests read the produced zip back, but they only assert what this
/// writer itself believes. To check the file against an independent OOXML reader, export one and run
/// <c>soffice --headless --convert-to csv --outdir /tmp/out book.xlsx</c>: LibreOffice refuses malformed
/// workbooks outright, and the CSV it emits shows whether numbers, booleans and both date styles survived as
/// types rather than as text.
/// </para>
/// <para>
/// <b>Types.</b> Numbers and booleans are written as typed cells (so they sum and filter in Excel), and
/// dates as a serial number plus a date <c>numFmt</c>. A <c>timestamptz</c> is the deliberate exception:
/// Excel has no concept of an offset, and silently dropping it would lose data, so it is exported as its ISO
/// text. Same for a date Excel can't represent (before its 1900 epoch).
/// </para>
/// </summary>
public static class XlsxWriter
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    // Style indices into cellXfs below.
    private const int StyleGeneral = 0, StyleHeader = 1, StyleDateTime = 2, StyleDate = 3, StyleTime = 4;

    /// <summary>Excel's own cell-text limit; longer values are truncated with an ellipsis rather than
    /// producing a file Excel refuses to open.</summary>
    private const int MaxCellText = 32_767;

    /// <summary>Write <paramref name="block"/> to <paramref name="output"/> as a one-sheet workbook.</summary>
    public static void Write(Stream output, TableBlock block, string sheetName)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Put(zip, "[Content_Types].xml", ContentTypes);
        Put(zip, "_rels/.rels", RootRels);
        Put(zip, "xl/workbook.xml", Workbook(SafeSheetName(sheetName)));
        Put(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
        Put(zip, "xl/styles.xml", Styles);
        WriteSheet(zip, block);
    }

    private static void Put(ZipArchive zip, string path, string content)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteSheet(ZipArchive zip, TableBlock block)
    {
        using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open();
        using var w = new StreamWriter(stream, new UTF8Encoding(false));
        w.Write($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{Main}">""");

        // Element order inside <worksheet> is fixed by the schema: sheetViews, then cols, then sheetData.
        w.Write("""<sheetViews><sheetView workbookViewId="0">"""
              + """<pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/>"""
              + "</sheetView></sheetViews>");
        WriteColumnWidths(w, block);

        w.Write("<sheetData>");
        w.Write("""<row r="1">""");
        for (var c = 0; c < block.Columns.Count; c++)
            WriteInlineString(w, Ref(c, 1), block.Columns[c].Name, StyleHeader);
        w.Write("</row>");

        for (var r = 0; r < block.Rows.Count; r++)
        {
            var rowNumber = r + 2; // 1 is the header
            w.Write($"""<row r="{rowNumber}">""");
            for (var c = 0; c < block.Columns.Count; c++) WriteCell(w, Ref(c, rowNumber), block.Value(r, c));
            w.Write("</row>");
        }
        w.Write("</sheetData></worksheet>");
    }

    /// <summary>Approximate column widths from the header and the first rows, so the sheet opens readable
    /// instead of every column at Excel's default 8.43 characters. Sampled, not exhaustive: a 200k-row export
    /// must not pay a full second pass to guess a width.</summary>
    private static void WriteColumnWidths(StreamWriter w, TableBlock block)
    {
        if (block.Columns.Count == 0) return;
        const int sample = 200;
        var rows = Math.Min(sample, block.Rows.Count);
        w.Write("<cols>");
        for (var c = 0; c < block.Columns.Count; c++)
        {
            var widest = block.Columns[c].Name.Length;
            for (var r = 0; r < rows; r++)
                widest = Math.Max(widest, (TableFormats.Text(block.Value(r, c)) ?? "").Length);
            var width = Math.Clamp(widest + 2, 8, 60);
            w.Write($"""<col min="{c + 1}" max="{c + 1}" width="{width}" customWidth="1"/>""");
        }
        w.Write("</cols>");
    }

    private static void WriteCell(StreamWriter w, string reference, object? value)
    {
        switch (value)
        {
            case null:
                return; // an absent <c> is an empty cell; writing one would only grow the file
            case bool b:
                w.Write($"""<c r="{reference}" t="b"><v>{(b ? 1 : 0)}</v></c>""");
                return;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double:
                w.Write($"""<c r="{reference}"><v>{Number(value)}</v></c>""");
                return;
            case DateTime dt when Serial(dt) is { } serial:
                var style = dt.TimeOfDay == TimeSpan.Zero ? StyleDate : StyleDateTime;
                w.Write($"""<c r="{reference}" s="{style}"><v>{Text(serial)}</v></c>""");
                return;
            case DateOnly d when Serial(d.ToDateTime(TimeOnly.MinValue)) is { } serial:
                w.Write($"""<c r="{reference}" s="{StyleDate}"><v>{Text(serial)}</v></c>""");
                return;
            case TimeOnly t:
                w.Write($"""<c r="{reference}" s="{StyleTime}"><v>{Text(t.ToTimeSpan().TotalDays)}</v></c>""");
                return;
            default:
                // Everything else — text, arrays, bytea, guids, and DateTimeOffset (Excel can't hold an
                // offset, so keeping the ISO text is the only lossless option).
                WriteInlineString(w, reference, TableFormats.Text(value) ?? "", StyleGeneral);
                return;
        }
    }

    /// <summary>Days since Excel's epoch, or null for a date outside what Excel can represent (its serial
    /// numbering starts at 1900 and has no room for anything earlier).</summary>
    private static double? Serial(DateTime value)
    {
        if (value.Year < 1900) return null;
        // 1899-12-30, not 1899-12-31: Excel treats 1900 as a leap year, and offsetting the epoch by a day is
        // the standard way to line up with it for every date from 1900-03-01 on.
        return (value - new DateTime(1899, 12, 30)).TotalDays;
    }

    private static void WriteInlineString(StreamWriter w, string reference, string text, int style)
    {
        var value = Clean(text);
        // Without xml:space="preserve" an XML reader is free to trim, so " 42" would arrive in Excel as "42".
        var edgeSpace = value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
        var preserve = edgeSpace ? " xml:space=\"preserve\"" : "";
        w.Write($"""<c r="{reference}" s="{style}" t="inlineStr"><is><t{preserve}>{Escape(value)}</t></is></c>""");
    }

    /// <summary>Drop the control characters XML 1.0 forbids and cap at Excel's cell limit. A single stray
    /// 0x01 in a text column is enough to make Excel declare the whole file corrupt.</summary>
    private static string Clean(string text)
    {
        var capped = text.Length > MaxCellText ? text[..(MaxCellText - 1)] + "…" : text;
        var sb = new StringBuilder(capped.Length);
        foreach (var ch in capped)
            if (ch is '\t' or '\n' or '\r' || (ch >= ' ' && ch != '￾' && ch != '￿'))
                sb.Append(ch);
        return sb.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Number(object value) => Convert.ToString(value, CultureInfo.InvariantCulture)!;

    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A1-style reference for a zero-based column index and a one-based row.</summary>
    internal static string Ref(int columnIndex, int row) => ColumnName(columnIndex) + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>Excel's column letters: 0 → A, 25 → Z, 26 → AA.</summary>
    internal static string ColumnName(int index)
    {
        var name = "";
        for (var i = index; ; i = i / 26 - 1)
        {
            name = (char)('A' + i % 26) + name;
            if (i < 26) return name;
        }
    }

    /// <summary>A sheet name Excel will accept: ≤31 characters, none of <c>[]:*?/\</c>, never blank.</summary>
    internal static string SafeSheetName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? '_' : ch);
        var cleaned = sb.ToString().Trim('\'', ' ');
        if (cleaned.Length == 0) cleaned = "Result";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    // ---- static parts ----------------------------------------------------------------------------

    private const string ContentTypes = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
        <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
        <Default Extension="xml" ContentType="application/xml"/>
        <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
        <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
        </Types>
        """;

    private const string RootRels = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="{PkgRel}">
        <Relationship Id="rId1" Type="{RelNs}/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string WorkbookRels = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="{PkgRel}">
        <Relationship Id="rId1" Type="{RelNs}/worksheet" Target="worksheets/sheet1.xml"/>
        <Relationship Id="rId2" Type="{RelNs}/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private static string Workbook(string sheetName) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="{Main}" xmlns:r="{RelNs}">
        <sheets><sheet name="{Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """;

    // numFmtId 164+ is the custom range. The three formats mirror CellFormat's ISO display, so a value looks
    // the same in Excel as it did in the grid.
    private const string Styles = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="{Main}">
        <numFmts count="3">
        <numFmt numFmtId="164" formatCode="yyyy\-mm\-dd\ hh:mm:ss"/>
        <numFmt numFmtId="165" formatCode="yyyy\-mm\-dd"/>
        <numFmt numFmtId="166" formatCode="hh:mm:ss"/>
        </numFmts>
        <fonts count="2">
        <font><sz val="11"/><name val="Calibri"/></font>
        <font><b/><sz val="11"/><name val="Calibri"/></font>
        </fonts>
        <fills count="2">
        <fill><patternFill patternType="none"/></fill>
        <fill><patternFill patternType="gray125"/></fill>
        </fills>
        <borders count="1"><border/></borders>
        <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
        <cellXfs count="5">
        <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
        <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
        <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
        <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
        <xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
        </cellXfs>
        </styleSheet>
        """;
}
