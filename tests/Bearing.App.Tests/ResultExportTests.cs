using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The export file itself: a hand-rolled xlsx that Excel will actually open (right parts, right element
/// order, typed cells, dates as dates), and the CSV/naming side. Asserted by reading the produced file back
/// — an export whose file is subtly malformed fails at the user's desk, not here, so "it didn't throw" is
/// not evidence (§4.4).
/// </summary>
public class ResultExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-export", Guid.NewGuid().ToString("N"));

    public ResultExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ResultSetViewModel Sample(EditTarget? target = null)
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int)),
            new ColumnDescriptor("name", "text", typeof(string)),
            new ColumnDescriptor("ok", "bool", typeof(bool)),
            new ColumnDescriptor("at", "timestamp", typeof(DateTime)),
        };
        var rows = new object?[][]
        {
            [1, "widget", true, new DateTime(2026, 8, 11, 14, 3, 22)],
            [2, null, false, null],
        };
        var result = new QueryResult(columns, rows, rows.Length, TimeSpan.Zero, null, null, false);
        return new ResultSetViewModel(result, "select * from t", pageable: false) { EditTarget = target };
    }

    private static string SheetXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return reader.ReadToEnd();
    }

    // ---- xlsx --------------------------------------------------------------------------------

    [Fact]
    public void An_xlsx_has_every_part_a_reader_looks_for()
    {
        var path = Path.Combine(_dir, "book.xlsx");
        ResultExport.Write(path, TableBlock.ForResult(Sample()), ExportFormat.Xlsx, "orders");

        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(
            new[]
            {
                "[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels",
                "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml",
            },
            zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));

        using var workbook = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Contains("name=\"orders\"", workbook.ReadToEnd());
    }

    [Fact]
    public void Worksheet_elements_are_in_schema_order()
    {
        // Excel rejects the file outright if sheetViews / cols / sheetData appear out of order — the kind of
        // mistake that looks fine until the first real open.
        var path = Path.Combine(_dir, "order.xlsx");
        ResultExport.Write(path, TableBlock.ForResult(Sample()), ExportFormat.Xlsx, "Result");
        var xml = SheetXml(path);

        Assert.True(xml.IndexOf("<sheetViews", StringComparison.Ordinal) < xml.IndexOf("<cols>", StringComparison.Ordinal));
        Assert.True(xml.IndexOf("<cols>", StringComparison.Ordinal) < xml.IndexOf("<sheetData>", StringComparison.Ordinal));
        Assert.Contains("state=\"frozen\"", xml);      // the header row stays put while scrolling
    }

    [Fact]
    public void Numbers_bools_and_dates_are_typed_cells_not_text()
    {
        var path = Path.Combine(_dir, "typed.xlsx");
        ResultExport.Write(path, TableBlock.ForResult(Sample()), ExportFormat.Xlsx, "Result");
        var xml = SheetXml(path);

        Assert.Contains("""<c r="A2"><v>1</v></c>""", xml);                    // int → bare number
        Assert.Contains("""<c r="C2" t="b"><v>1</v></c>""", xml);              // bool → boolean cell
        Assert.Contains("""<c r="B2" s="0" t="inlineStr"><is><t>widget</t></is></c>""", xml);
        // 2026-08-11 14:03:22 is 46245 days after Excel's epoch plus the time as a fraction.
        var serial = (new DateTime(2026, 8, 11, 14, 3, 22) - new DateTime(1899, 12, 30)).TotalDays;
        Assert.Contains($"""<c r="D2" s="2"><v>{serial.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}</v></c>""", xml);
        // A NULL writes no cell at all; the row simply skips that reference.
        Assert.DoesNotContain("r=\"D3\"", xml);
        Assert.DoesNotContain("r=\"B3\"", xml);
    }

    [Fact]
    public void A_timestamptz_stays_text_because_excel_has_no_offset()
    {
        var columns = new[] { new ColumnDescriptor("at", "timestamptz", typeof(DateTimeOffset)) };
        var rows = new object?[][] { [new DateTimeOffset(2026, 8, 11, 14, 3, 22, TimeSpan.FromHours(2))] };
        var rs = new ResultSetViewModel(
            new QueryResult(columns, rows, 1, TimeSpan.Zero, null, null, false), null, pageable: false);

        var path = Path.Combine(_dir, "tz.xlsx");
        ResultExport.Write(path, TableBlock.ForResult(rs), ExportFormat.Xlsx, "Result");

        // Converting it to a serial would silently drop "+02:00"; the ISO text keeps the whole value.
        Assert.Contains("2026-08-11 14:03:22+02:00", SheetXml(path));
    }

    [Fact]
    public void Control_characters_are_stripped_so_excel_can_open_the_file()
    {
        // A single 0x01 in a text column makes Excel declare the whole workbook corrupt.
        var columns = new[] { new ColumnDescriptor("a", "text", typeof(string)) };
        var rows = new object?[][] { ["bad\u0001value\tkept"] };
        var rs = new ResultSetViewModel(
            new QueryResult(columns, rows, 1, TimeSpan.Zero, null, null, false), null, pageable: false);

        var path = Path.Combine(_dir, "ctrl.xlsx");
        ResultExport.Write(path, TableBlock.ForResult(rs), ExportFormat.Xlsx, "Result");
        var xml = SheetXml(path);

        // Ordinal on purpose: xUnit's default overload compares culture-aware, and ICU treats U+0001 as
        // an *ignorable* character — so the default "finds" it in every string and this assertion could
        // never fail, whatever the writer did.
        Assert.DoesNotContain("\u0001", xml, StringComparison.Ordinal);
        Assert.Contains("badvalue\tkept", xml, StringComparison.Ordinal); // 0x01 gone, tab kept
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(702, "AAA")]
    public void Column_letters_carry_past_z(int index, string expected)
        => Assert.Equal(expected, XlsxWriter.ColumnName(index));

    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("public/orders", "public_orders")]
    [InlineData("a[b]c:d*e?f", "a_b_c_d_e_f")]
    [InlineData("", "Result")]
    [InlineData("a-very-long-table-name-that-excel-will-not-accept-at-all", "a-very-long-table-name-that-exc")]
    public void Sheet_names_are_made_acceptable_to_excel(string given, string expected)
        => Assert.Equal(expected, XlsxWriter.SafeSheetName(given));

    // ---- CSV ---------------------------------------------------------------------------------

    [Fact]
    public void A_csv_export_is_utf8_with_a_bom()
    {
        var columns = new[] { new ColumnDescriptor("name", "text", typeof(string)) };
        var rows = new object?[][] { ["Kajfeš"] };
        var rs = new ResultSetViewModel(
            new QueryResult(columns, rows, 1, TimeSpan.Zero, null, null, false), null, pageable: false);

        var path = Path.Combine(_dir, "names.csv");
        ResultExport.Write(path, TableBlock.ForResult(rs), ExportFormat.Csv, "Result");

        var bytes = File.ReadAllBytes(path);
        // Without the BOM Excel guesses the system code page and mangles every non-ASCII value.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Contains("Kajfeš", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void A_write_leaves_no_temp_file_behind_and_replaces_an_existing_export()
    {
        var path = Path.Combine(_dir, "twice.csv");
        File.WriteAllText(path, "stale");
        ResultExport.Write(path, TableBlock.ForResult(Sample()), ExportFormat.Csv, "Result");

        Assert.DoesNotContain("stale", File.ReadAllText(path));
        Assert.Equal(new[] { "twice.csv" }, Directory.GetFiles(_dir).Select(Path.GetFileName)); // no .tmp
    }

    // ---- naming ------------------------------------------------------------------------------

    [Fact]
    public void The_suggested_name_prefers_the_source_table_then_the_tab()
    {
        var at = new DateTime(2026, 8, 11, 14, 3, 22);
        var target = new EditTarget("public", "orders", [new EditableColumn(0, "id", true)]);

        Assert.Equal("orders-20260811-140322.csv",
            ResultExport.SuggestedName(Sample(target), "query 1", at, ExportFormat.Csv));
        // No single table (a join, a view) → the tab's name, sanitised, with the right extension.
        Assert.Equal("my-query-20260811-140322.xlsx",
            ResultExport.SuggestedName(Sample(), "my query", at, ExportFormat.Xlsx));
        Assert.Equal("result-20260811-140322.csv",
            ResultExport.SuggestedName(Sample(), null, at, ExportFormat.Csv));
    }

    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("a/b\\c", "a-b-c")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("...", "result")]
    public void Names_are_slugged_so_the_picker_never_gets_an_invalid_one(string given, string expected)
        => Assert.Equal(expected, ResultExport.Slug(given));
}
