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

        var csv = await ConvertToCsvAsync(soffice!, book);

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

        // soffice --convert-to csv exports only the *first* sheet, so the round trip is per sheet: convert the
        // same book three times, selecting a different sheet each time. That the sheet names themselves are
        // right is what WorkbookExportTests checks; this checks each one holds its own rows.
        var book = Path.Combine(_dir, "multi.xlsx");
        ResultExport.WriteWorkbook(book, [
            Sheet("first", [[1, "alpha"]]),
            Sheet("second", [[2, "beta"]]),
            Sheet("third", [[3, "gamma"]]),
        ]);

        for (var sheet = 1; sheet <= 3; sheet++)
        {
            var csv = await ConvertToCsvAsync(soffice!, book, sheet);
            var expected = sheet switch { 1 => "alpha", 2 => "beta", _ => "gamma" };
            Assert.Contains(expected, csv);
        }
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

    private async Task<string> ConvertToCsvAsync(string soffice, string book, int sheet = 1)
    {
        // A private profile directory per conversion: soffice refuses to start a second instance against a
        // profile another one holds, and the suite must not depend on the developer's LibreOffice being closed.
        var profile = Path.Combine(_dir, $"profile-{Guid.NewGuid():N}");
        var outDir = Path.Combine(_dir, $"out-{sheet}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);

        // 44,34,76,{sheet} is the Calc CSV filter's token list: comma separator, double-quote text delimiter,
        // UTF-8, and the 1-based sheet to export.
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
        start.ArgumentList.Add($"csv:Text - txt - csv (StarCalc):44,34,76,{sheet}");
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
        Skip.If(produced.Length == 0 && LooksLikeAnEnvironmentProblem(await stderr),
            "soffice is installed but could not run headlessly here (it reported a startup problem, not a "
            + "problem with the workbook).");
        Assert.True(produced.Length > 0,
            $"LibreOffice produced no CSV from the workbook — it could not open it.\n"
            + $"stdout: {await stdout}\nstderr: {await stderr}");

        return await File.ReadAllTextAsync(produced[0]);
    }

    /// <summary>Tell "the workbook is bad" apart from "this machine cannot run soffice". Only the first is a
    /// failure; the second is the same skip as a missing binary, one step later.</summary>
    private static bool LooksLikeAnEnvironmentProblem(string stderr)
        => stderr.Contains("javaldx", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("cannot open display", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("Application Error", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("UserInstallation", StringComparison.OrdinalIgnoreCase);

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
