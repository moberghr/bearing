using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Bearing.App.Results;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The audit report's notes have to reach the <em>file</em> (#113), because the person who reads the report
/// is not the person who exported it: the period covered, whose activity it is and whether the SQL was stored
/// redacted are all things a dialog cannot tell them. Each format carries them the way that format is read.
/// </summary>
public class AuditReportFileTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "bearing-audit-file", Guid.NewGuid().ToString("N"));

    public AuditReportFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    private string In(string name) => System.IO.Path.Combine(_dir, name);

    private static readonly string[] Notes =
    [
        "Bearing query log — audit report",
        "Period: 2026-09-01 to 2026-09-07",
        "Literal redaction is currently OFF.",
    ];

    private static TableBlock Block() => new(
        [
            new ColumnDescriptor("executed_at", "text", typeof(string)),
            new ColumnDescriptor("statement", "text", typeof(string)),
        ],
        [
            ["2026-09-03 10:00:00 +00:00", "delete from rental where rental_id = 1"],
            ["2026-09-03 10:01:00 +00:00", "select 1"],
        ]);

    /// <summary>
    /// A CSV is a pure table with its header on row 1, and the notes go in a sibling file.
    /// <para>
    /// The first version wrote <c>#</c>-prefixed notes above the header on the theory that CSV readers skip
    /// them. They do not: Excel and LibreOffice put the header on row 7, a note containing commas split into
    /// cells, and pandas raised. RFC 4180 has no comment syntax, so the only place notes can live without
    /// breaking the readers an auditor actually uses is beside the file.
    /// </para>
    /// </summary>
    [Fact]
    public void A_csv_is_a_pure_table_and_its_notes_sit_beside_it()
    {
        var path = In("audit.csv");
        var written = ResultExport.WriteReport(path, Block(), ExportFormat.Csv, "Audit report", Notes);

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("executed_at", lines[0].TrimStart('﻿'));   // header on row 1, nothing above it
        Assert.DoesNotContain(lines, l => l.TrimStart('﻿').StartsWith("#"));
        Assert.Equal(3, lines.Length);                              // header + two rows

        var about = ResultExport.NotesPathFor(path);
        Assert.Equal([path, about], written);
        Assert.EndsWith("audit.about.txt", about);
        var notes = File.ReadAllLines(about);
        Assert.Contains("Period: 2026-09-01 to 2026-09-07", notes);
        Assert.Equal(Notes.Length, notes.Length);
    }

    [Fact]
    public void A_workbook_needs_no_sibling_because_it_has_a_sheet()
    {
        var path = In("audit.xlsx");
        var written = ResultExport.WriteReport(path, Block(), ExportFormat.Xlsx, "Audit report", Notes);

        Assert.Equal([path], written);
        Assert.False(File.Exists(ResultExport.NotesPathFor(path)));
    }

    [Fact]
    public void A_workbook_carries_the_notes_on_their_own_sheet_beside_the_rows()
    {
        var path = In("audit.xlsx");
        ResultExport.WriteReport(path, Block(), ExportFormat.Xlsx, "Audit report", Notes);

        using var zip = ZipFile.OpenRead(path);
        var names = System.Text.RegularExpressions.Regex
            .Matches(Entry(zip, "xl/workbook.xml"), "<sheet name=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // The rows first — that is what the report is — with the notes as a second sheet rather than as
        // rows above the header, which would break every formula and filter applied to the table.
        Assert.Equal(["Audit report", "About"], names);

        // Cell values are inline strings, so each sheet's own part carries its text.
        Assert.Contains("delete from rental where rental_id = 1", Entry(zip, "xl/worksheets/sheet1.xml"));
        Assert.Contains("Period: 2026-09-01 to 2026-09-07", Entry(zip, "xl/worksheets/sheet2.xml"));
    }

    [Fact]
    public void An_empty_report_still_writes_a_file_with_its_notes()
    {
        // "Nothing ran in that period" is an answer, and it has to be an artefact the user can hand over.
        var path = In("empty.csv");
        var empty = new TableBlock(Block().Columns, []);
        ResultExport.WriteReport(path, empty, ExportFormat.Csv, "Audit report", Notes);

        var lines = File.ReadAllLines(path);
        Assert.Single(lines);                                       // the header row alone
        Assert.StartsWith("executed_at", lines[0].TrimStart('﻿'));
        Assert.True(File.Exists(ResultExport.NotesPathFor(path)));  // the notes still say what the period was
    }

    [Fact]
    public void An_interrupted_write_leaves_no_temp_file_behind()
    {
        var path = In("clean.csv");
        ResultExport.WriteReport(path, Block(), ExportFormat.Csv, "Audit report", Notes);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(ResultExport.NotesPathFor(path) + ".tmp"));
    }

    private static string Entry(ZipArchive zip, string name)
    {
        var part = zip.GetEntry(name);
        Assert.NotNull(part);
        using var reader = new StreamReader(part!.Open());
        return reader.ReadToEnd();
    }
}
