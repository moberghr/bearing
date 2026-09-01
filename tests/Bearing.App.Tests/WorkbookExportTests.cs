using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Bearing.App.Results;
using Bearing.Demo;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// A whole run as one workbook, a sheet per result set (#12). One sheet used to be baked into four parts that
/// all have to agree — the content type, the workbook's sheet list, the relationship, and the zip entry path —
/// so most of what follows reads the produced file back and checks they still do.
/// <para>
/// "It didn't throw" is not evidence here: a workbook whose parts disagree fails when Excel opens it, at the
/// user's desk (§4.4). The writer's own comment names the independent check —
/// <c>soffice --headless --convert-to csv</c> — for changes these assertions cannot reach.
/// </para>
/// </summary>
public class WorkbookExportTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "bearing-workbook", Guid.NewGuid().ToString("N"));

    public WorkbookExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    /// <summary>A path in this test's own directory. Named In() rather than Path() so it does not
    /// shadow <see cref="System.IO.Path"/> in the members above.</summary>
    private string In(string name) => System.IO.Path.Combine(_dir, name);

    // ---- fixtures --------------------------------------------------------------------------------

    private static ResultSetViewModel Set(string column, int rows, string? table = null)
    {
        var data = Enumerable.Range(1, rows).Select(i => new object?[] { i, $"{column}-{i}" }).ToList();
        var result = new QueryResult(
            [new ColumnDescriptor("id", "int4", typeof(int)), new ColumnDescriptor(column, "text", typeof(string))],
            data, data.Count, TimeSpan.Zero, null, null, false);
        return new ResultSetViewModel(result, $"select * from {column}", pageable: false)
        {
            EditTarget = table is null ? null : new EditTarget("public", table, []),
        };
    }

    private static ResultSetViewModel Message() => new(
        new QueryResult([], [], 3, TimeSpan.Zero, "UPDATE 3", null, false), null, pageable: false);

    private static ResultSetViewModel Failure() => new(
        new QueryResult([], [], 0, TimeSpan.Zero, null, new QueryError("nope", "42P01", 1), false), null, false);

    private static XlsxWriter.Sheet Sheet(string name, int rows = 2)
        => new(TableBlock.ForResult(Set("v", rows)), name);

    // ---- reading the file back -------------------------------------------------------------------

    private static ZipArchive Open(string path) => ZipFile.OpenRead(path);

    private static string Read(ZipArchive zip, string entry)
    {
        var part = zip.GetEntry(entry);
        Assert.NotNull(part);
        using var reader = new StreamReader(part!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>The sheet names the workbook declares, in order.</summary>
    private static List<string> SheetNamesOf(ZipArchive zip)
        => System.Text.RegularExpressions.Regex
            .Matches(Read(zip, "xl/workbook.xml"), "<sheet name=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

    // ---- the workbook ----------------------------------------------------------------------------

    [Fact]
    public void Every_sheet_gets_a_part_a_content_type_a_relationship_and_a_place_in_the_list()
    {
        // The four places that used to know there was exactly one sheet. Any one of them out of step and Excel
        // rejects the file.
        var path = In("run.xlsx");
        ResultExport.WriteWorkbook(path, [Sheet("first"), Sheet("second"), Sheet("third")]);

        using var zip = Open(path);
        for (var n = 1; n <= 3; n++)
        {
            Assert.NotNull(zip.GetEntry($"xl/worksheets/sheet{n}.xml"));
            Assert.Contains($"/xl/worksheets/sheet{n}.xml", Read(zip, "[Content_Types].xml"));
            Assert.Contains($"Target=\"worksheets/sheet{n}.xml\"", Read(zip, "xl/_rels/workbook.xml.rels"));
        }
        Assert.Equal(["first", "second", "third"], SheetNamesOf(zip));
    }

    [Fact]
    public void The_styles_relationship_still_resolves_with_several_sheets()
    {
        // Styles used to be rId2 with one sheet. With three, rId2 is a sheet — so a fixed id would have made
        // the workbook point a sheet at styles.xml and lose every date format.
        var path = In("styles.xlsx");
        ResultExport.WriteWorkbook(path, [Sheet("a"), Sheet("b"), Sheet("c")]);

        using var zip = Open(path);
        var rels = Read(zip, "xl/_rels/workbook.xml.rels");
        // Styles lands after the sheets, so with three of them it is rId4 and not rId2.
        Assert.Contains("Id=\"rId4\"", rels);
        Assert.Contains("Target=\"styles.xml\"", rels);
        Assert.NotNull(zip.GetEntry("xl/styles.xml"));

        // And no id is used twice, which is the way this goes wrong quietly.
        var ids = System.Text.RegularExpressions.Regex.Matches(rels, "Id=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Distinct(), ids);
    }

    [Fact]
    public void Each_sheet_holds_its_own_rows()
    {
        var path = In("rows.xlsx");
        ResultExport.WriteWorkbook(path, [Sheet("small", rows: 2), Sheet("big", rows: 5)]);

        using var zip = Open(path);
        // Header plus data rows, per sheet, and not the other sheet's.
        Assert.Equal(3, Rows(Read(zip, "xl/worksheets/sheet1.xml")));
        Assert.Equal(6, Rows(Read(zip, "xl/worksheets/sheet2.xml")));

        static int Rows(string xml) => System.Text.RegularExpressions.Regex.Matches(xml, "<row ").Count;
    }

    [Fact]
    public void A_single_sheet_workbook_is_written_exactly_as_before()
    {
        // The old call is a wrapper over the new one now, so the one-sheet file must not have moved.
        var single = In("one.xlsx");
        var block = TableBlock.ForResult(Set("v", 3));
        ResultExport.Write(single, block, ExportFormat.Xlsx, "orders");

        using var zip = Open(single);
        Assert.Equal(["orders"], SheetNamesOf(zip));
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
        Assert.Contains("Target=\"styles.xml\"", Read(zip, "xl/_rels/workbook.xml.rels"));
    }

    [Fact]
    public void A_workbook_with_no_sheets_is_refused_rather_than_written_empty()
    {
        // Excel cannot open a workbook with no sheets, so producing one would be a file that only fails later.
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write(new MemoryStream(), []));
    }

    [Fact]
    public void An_interrupted_workbook_does_not_replace_a_good_one()
    {
        // Temp-file-and-move, as every other write here: a workbook cut off halfway must not sit on top of the
        // previous export looking complete.
        var path = In("atomic.xlsx");
        ResultExport.WriteWorkbook(path, [Sheet("good")]);
        var good = File.ReadAllBytes(path);

        Assert.Throws<ArgumentException>(() => ResultExport.WriteWorkbook(path, []));

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"), "the temp file was left behind");
    }

    // ---- which results become sheets, and what they are called -----------------------------------

    [Fact]
    public void Only_the_grid_results_become_sheets()
    {
        // A statement message and an error are not sheets: an empty tab named after a DELETE is worse than its
        // absence.
        var sheets = ResultExport.RunSheets([Set("a", 2, "orders"), Message(), Failure(), Set("b", 2, "items")]);

        Assert.Equal(["orders", "items"], sheets.Select(s => s.Name));
    }

    [Fact]
    public void An_unnamed_result_is_numbered_by_its_place_in_the_run()
    {
        // Not by its place among the sheets: "Result 3" has to be the third result on screen, or the workbook
        // and the results pane disagree about which one you are looking at.
        var sheets = ResultExport.RunSheets([Message(), Set("a", 1), Set("b", 1)]);

        Assert.Equal(["Result 2", "Result 3"], sheets.Select(s => s.Name));
    }

    [Fact]
    public void A_run_of_nothing_but_messages_yields_no_sheets()
        => Assert.Empty(ResultExport.RunSheets([Message(), Failure()]));

    [Fact]
    public void The_workbook_is_named_after_the_tab()
    {
        var name = ResultExport.SuggestedRunName("daily revenue", new DateTime(2026, 9, 1, 14, 30, 5));

        Assert.Equal("daily-revenue-20260901-143005.xlsx", name);
    }

    [Fact]
    public void A_nameless_tab_still_produces_a_usable_file_name()
        => Assert.StartsWith("run-", ResultExport.SuggestedRunName("   ", DateTime.Now));

    // ---- the collision trap ----------------------------------------------------------------------

    [Fact]
    public void Two_results_from_the_same_table_do_not_collide()
    {
        // Excel refuses a workbook with duplicate sheet names, and one run selecting twice from one table is
        // entirely normal.
        var path = In("dupes.xlsx");
        ResultExport.WriteWorkbook(path, ResultExport.RunSheets(
            [Set("a", 1, "orders"), Set("b", 1, "orders"), Set("c", 1, "orders")]));

        using var zip = Open(path);
        Assert.Equal(["orders", "orders (2)", "orders (3)"], SheetNamesOf(zip));
    }

    [Fact]
    public void Sanitizing_two_long_names_to_the_same_31_characters_does_not_collide()
    {
        // The subtle half: sanitizing is itself a source of collisions, so de-duplication has to run after it.
        var long1 = new string('x', 40) + "-one";
        var long2 = new string('x', 40) + "-two";

        var names = XlsxWriter.SafeSheetName(long1) == XlsxWriter.SafeSheetName(long2)
            ? Written(long1, long2)
            : throw new InvalidOperationException("the fixture no longer truncates to the same name");

        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.True(n.Length <= 31, $"'{n}' is {n.Length} characters"));
    }

    [Fact]
    public void Names_differing_only_in_case_still_collide_for_excel()
    {
        // Excel compares sheet names case-insensitively, so "Orders" and "orders" are one name to it.
        var names = Written("Orders", "orders");

        Assert.Equal(["Orders", "orders (2)"], names);
    }

    [Fact]
    public void A_name_that_is_only_illegal_characters_becomes_a_usable_pair()
    {
        var names = Written("[]", "*?");

        Assert.Equal(2, names.Distinct().Count());
        Assert.All(names, n => Assert.DoesNotContain('[', n));
    }

    [Fact]
    public void A_suffix_never_pushes_a_name_over_excels_limit()
    {
        var names = Written(new string('a', 31), new string('a', 31), new string('a', 31));

        Assert.All(names, n => Assert.True(n.Length <= 31, $"'{n}' is {n.Length} characters"));
        Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>The names a workbook ends up declaring for these wanted names.</summary>
    private List<string> Written(params string[] wanted)
    {
        var path = In($"names-{Guid.NewGuid():N}.xlsx");
        ResultExport.WriteWorkbook(path, wanted.Select(n => Sheet(n, 1)).ToList());
        using var zip = Open(path);
        return SheetNamesOf(zip);
    }

    // ---- against the demo fixtures ---------------------------------------------------------------

    [Fact]
    public void A_demo_run_exports_as_a_workbook_of_its_grids()
    {
        // The whole shape end to end, off the fixtures: five results, three of them grids (#63).
        var results = ResultSetBuilder.BuildResultSets(DemoCatalog.Run(), "select …", DemoCatalog.Snapshot());
        var path = In("demo.xlsx");

        ResultExport.WriteWorkbook(path, ResultExport.RunSheets(results));

        using var zip = Open(path);
        Assert.Equal(results.Count(r => r.HasGrid), SheetNamesOf(zip).Count);
        Assert.Contains("store", SheetNamesOf(zip));
        Assert.Contains("payment", SheetNamesOf(zip));
    }
}
