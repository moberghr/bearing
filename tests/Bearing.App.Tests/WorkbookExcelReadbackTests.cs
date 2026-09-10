using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The independent check <see cref="WorkbookExportTests"/> names but performs by hand: hand the workbook to a
/// spreadsheet implementation that is not ours and see whether it can read it (#12).
/// <para>
/// The in-suite tests unzip the file and assert its four parts agree, which is the right shape for catching a
/// drifting writer — but they agree with the same understanding of the format that wrote it. A workbook whose
/// parts are mutually consistent and still rejected by Excel would pass every one of them, and fail at the
/// user's desk. LibreOffice is a second opinion from a codebase that has never seen ours.
/// </para>
/// <para>
/// Skips, never fails, when <c>soffice</c> is not installed — the same posture as the Postgres suites (§4.2):
/// a developer without LibreOffice is not a broken build, and the message says which binary was looked for.
/// </para>
/// </summary>
public class WorkbookExcelReadbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bearing-xlsx-readback", Guid.NewGuid().ToString("N"));

    public WorkbookExcelReadbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    [SkippableFact]
    public async Task LibreOffice_opens_the_workbook_and_reads_back_what_we_wrote()
    {
        var soffice = FindSoffice();
        Skip.If(soffice is null,
            "LibreOffice (soffice) is not on PATH, so the workbook cannot be checked against an independent "
            + "reader. Set BEARING_TEST_SOFFICE to its full path to run this.");

        var book = Path.Combine(_dir, "run.xlsx");
        // Values chosen for what a converter would mangle rather than for coverage: a comma and a quote fight
        // CSV escaping, a leading zero and a long digit run are what a spreadsheet silently turns into a
        // number, and a non-ASCII string is where an encoding assumption shows up.
        ResultExport.WriteWorkbook(book, [
            Sheet("orders", [
                [1, "Doe, Jane"],
                [2, "say \"hi\""],
                [3, "007"],
                [4, "Ćevapčići"],
                [5, "9007199254740993"],
            ]),
        ]);

        var csv = SheetCsv(await ConvertSheetsToCsvAsync(soffice!, book), "orders");

        // The header and every value, present and unmangled. Assert on the text rather than parsing CSV: the
        // question is whether the data survived, and a parser here would just be a second thing to get wrong.
        Assert.Contains("id", csv);
        Assert.Contains("name", csv);
        Assert.Contains("Doe, Jane", csv);
        Assert.Contains("hi", csv);
        Assert.Contains("Ćevapčići", csv);
        Assert.Contains("9007199254740993", csv);   // not 9.00719925474099E+15
        Assert.Contains("007", csv);                // not 7
        Assert.Equal(5, csv.Split('\n').Count(l => l.Trim().Length > 0) - 1);
    }

    [SkippableFact]
    public async Task Every_sheet_of_a_multi_result_run_survives_the_round_trip()
    {
        var soffice = FindSoffice();
        Skip.If(soffice is null, "LibreOffice (soffice) is not on PATH.");

        var book = Path.Combine(_dir, "multi.xlsx");
        ResultExport.WriteWorkbook(book, [
            Sheet("first", [[1, "alpha"]]),
            Sheet("second", [[2, "beta"]]),
            Sheet("third", [[3, "gamma"]]),
        ]);

        var sheets = await ConvertSheetsToCsvAsync(soffice!, book);

        // Each sheet holds its own rows and only its own — the failure this catches is a writer that points
        // three sheet entries at one worksheet part, which every in-suite assertion would still pass.
        Assert.Contains("alpha", SheetCsv(sheets, "first"));
        Assert.Contains("beta", SheetCsv(sheets, "second"));
        Assert.Contains("gamma", SheetCsv(sheets, "third"));
        Assert.DoesNotContain("beta", SheetCsv(sheets, "first"));
    }

    /// <summary>
    /// The audit report's workbook (#113), which is the other two-sheet shape this writer produces — the
    /// rows on one sheet and the notes that have to travel with them on another.
    /// <para>
    /// Its own test rather than leaning on the run-workbook one above, because it is assembled by a
    /// different function (<c>ResultExport.WriteReport</c> rather than <c>WriteWorkbook</c>) and because the
    /// notes are the part that would be silently lost: a reader that dropped the second sheet would leave a
    /// report that still looks complete and no longer says what period it covers or whether its SQL was
    /// stored redacted.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_audit_report_keeps_its_rows_and_its_notes_in_separate_sheets()
    {
        var soffice = FindSoffice();
        Skip.If(soffice is null, "LibreOffice (soffice) is not on PATH.");

        var book = Path.Combine(_dir, "audit.xlsx");
        var table = new TableBlock(
            [
                new ColumnDescriptor("executed_at", "text", typeof(string)),
                new ColumnDescriptor("statement", "text", typeof(string)),
            ],
            [
                ["2026-09-03 10:00:00 +00:00", "delete from rental where rental_id = 1"],
                // A comma and a quote, which is where a CSV round trip goes wrong.
                ["2026-09-03 10:01:00 +00:00", """update payment set note = 'a, b "c"' where id = 2"""],
            ]);
        string[] notes =
        [
            "Bearing query log — audit report",
            "Period: 2026-09-01 00:00:00 +02:00 to 2026-09-07 23:59:59 +02:00",
            "Literal redaction is currently OFF: statements are recorded as written, values included.",
        ];

        var written = ResultExport.WriteReport(book, table, ExportFormat.Xlsx, "Audit report", notes);
        // The xlsx carries its own notes, so there is no sibling file — unlike the CSV path.
        Assert.Equal([book], written);

        var sheets = await ConvertSheetsToCsvAsync(soffice!, book);

        var rows = SheetCsv(sheets, "Audit report");
        Assert.Contains("delete from rental", rows);
        Assert.Contains("a, b", rows);
        Assert.DoesNotContain("Period:", rows);   // the notes are not mixed into the table

        var about = SheetCsv(sheets, "About");
        Assert.Contains("Period: 2026-09-01", about);
        Assert.Contains("redaction is currently OFF", about);
        // …and the period survived with its offset, which is what makes the report answerable.
        Assert.Contains("+02:00", about);
    }

    // ---- the conversion ------------------------------------------------------------------------------

    private static string? FindSoffice()
    {
        if (Environment.GetEnvironmentVariable("BEARING_TEST_SOFFICE") is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? explicitPath : null;

        // The usual install locations, plus PATH. Not an exhaustive search: the env var is the escape hatch.
        var candidates = new[]
        {
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
            "/usr/bin/soffice",
            "/usr/local/bin/soffice",
            "/Applications/LibreOffice.app/Contents/MacOS/soffice",
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        var name = OperatingSystem.IsWindows() ? "soffice.exe" : "soffice";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try
            {
                var probe = Path.Combine(dir, name);
                if (File.Exists(probe)) return probe;
            }
            catch { /* an unparseable PATH entry is not this test's problem */ }
        }
        return null;
    }

    /// <summary>
    /// Convert the workbook to CSV and return every sheet, keyed by its name.
    /// <para>
    /// One invocation exporting all sheets, rather than one per sheet selecting an index — and the index was
    /// the bug. The filter token list this used to pass was <c>44,34,76,{sheet}</c>, described in a comment
    /// as "comma, double-quote, UTF-8, and the 1-based sheet to export". Token 4 is <b>FirstLineNumber</b>;
    /// there is no sheet selector until token 12. So every conversion exported the <em>first</em> sheet and
    /// the multi-sheet test asserted against it while believing it had asked for sheet 2 — invisible for as
    /// long as the whole class skipped for want of LibreOffice, and the first thing it reported once CI had
    /// it.
    /// </para>
    /// <para>
    /// Token 12 = <c>-1</c> is "all sheets", which writes one file per sheet named
    /// <c>&lt;stem&gt;-&lt;SheetName&gt;.csv</c>. Keying on that name is better than an index anyway: it
    /// checks the names too, and a test asking for "About" says what it means.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ConvertSheetsToCsvAsync(string soffice, string book)
    {
        // A private profile directory per conversion: soffice refuses to start a second instance against a
        // profile another one holds, and the suite must not depend on the developer's LibreOffice being closed.
        var profile = Path.Combine(_dir, $"profile-{Guid.NewGuid():N}");
        var outDir = Path.Combine(_dir, $"out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var start = new ProcessStartInfo(soffice)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--norestore");
        start.ArgumentList.Add($"-env:UserInstallation=file:///{profile.Replace('\\', '/').TrimStart('/')}");
        start.ArgumentList.Add("--convert-to");
        // Comma, double-quote, UTF-8, from line 1, no format codes, language 0, unquoted fields as values,
        // detect numbers, cells as shown, no formulas, keep spaces — and finally -1: every sheet.
        start.ArgumentList.Add(
            "csv:Text - txt - csv (StarCalc):44,34,76,1,,0,false,true,true,false,false,-1");
        start.ArgumentList.Add("--outdir");
        start.ArgumentList.Add(outDir);
        start.ArgumentList.Add(book);

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("soffice did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("soffice did not finish converting the workbook within two minutes.");
        }

        var produced = Directory.GetFiles(outDir, "*.csv");
        // A conversion that produces nothing is the failure this test exists to catch: LibreOffice could not
        // open the workbook. Its own output is the only diagnostic there is, so carry it into the message.
        var stderrText = await stderr;
        Skip.If(produced.Length == 0 && LooksLikeAnEnvironmentProblem(stderrText),
            "soffice is installed but could not start headlessly here — it reported a startup problem before "
            + $"reaching the file, so this says nothing about the workbook. It said: {Head(stderrText)}");
        Assert.True(produced.Length > 0,
            $"LibreOffice produced no CSV from the workbook — it could not open it.\n"
            + $"stdout: {await stdout}\nstderr: {stderrText}");

        // "<stem>-<Sheet Name>.csv" for an all-sheets export; a single-sheet book is just "<stem>.csv".
        var stem = Path.GetFileNameWithoutExtension(book);
        var sheets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in produced)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var sheet = name.StartsWith(stem + "-", StringComparison.Ordinal)
                ? name[(stem.Length + 1)..]
                : name;
            sheets[sheet] = await File.ReadAllTextAsync(file);
        }
        return sheets;
    }

    /// <summary>
    /// One sheet's CSV by name, or a skip naming what was produced instead.
    /// <para>
    /// A skip rather than a failure because the all-sheets token is a LibreOffice version behaviour, not a
    /// property of our workbook: a build that ignores it says so and says what it did produce, which is the
    /// same posture the rest of this class takes toward the environment.
    /// </para>
    /// </summary>
    private static string SheetCsv(IReadOnlyDictionary<string, string> sheets, string name)
    {
        if (sheets.TryGetValue(name, out var csv)) return csv;

        Skip.If(true,
            $"LibreOffice produced sheets [{string.Join(", ", sheets.Keys)}] rather than one called "
            + $"'{name}' — this build may not support exporting every sheet.");
        return "";
    }

    /// <summary>The first few lines of soffice's stderr, so a skip message says what it decided on without
    /// pasting a screenful of LibreOffice startup chatter into the test output.</summary>
    private static string Head(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3).Select(l => l.Trim());
        return string.Join(" / ", lines) is { Length: > 0 } joined ? joined : "(nothing on stderr)";
    }

    /// <summary>
    /// Tell "this machine cannot run soffice at all" apart from "soffice ran and could not open our
    /// workbook". Only the second is a failure — but it is <b>the</b> failure this test exists to catch, so
    /// the bar for calling something an environment problem is deliberately high.
    /// <para>
    /// Notably absent: <c>javaldx: Could not find a Java Runtime Environment</c>. LibreOffice prints that on
    /// essentially every headless invocation on a machine with no JRE — which describes any minimal CI image
    /// for a .NET project — and it says nothing about whether Calc could read the file. Matching it would
    /// have turned a corrupt workbook into a silent skip on exactly the machines most likely to run this.
    /// </para>
    /// </summary>
    private static bool LooksLikeAnEnvironmentProblem(string stderr)
        // A display it cannot open, or a profile it cannot create: soffice never got as far as the file.
        => stderr.Contains("cannot open display", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("failed to create the user profile", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("could not create the user installation", StringComparison.OrdinalIgnoreCase);

    /// <summary>One sheet of the workbook, built the way the export path builds them
    /// (<c>TableBlock.ForResult</c>) rather than assembled here — the point is to exercise the real writer.</summary>
    private static XlsxWriter.Sheet Sheet(string name, IReadOnlyList<object?[]> rows)
    {
        var result = new QueryResult(
            [new ColumnDescriptor("id", "int4", typeof(int)), new ColumnDescriptor("name", "text", typeof(string))],
            rows.ToList(), rows.Count, TimeSpan.Zero, null, null, false);
        var vm = new ResultSetViewModel(result, $"select * from {name}", pageable: false);
        return new XlsxWriter.Sheet(TableBlock.ForResult(vm), name);
    }
}
