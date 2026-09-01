using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;
using Xunit.Abstractions;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Renders the screens that can only be judged by eye and writes them out as PNGs. Opt-in — set
/// <c>BEARING_UI_CAPTURE_DIR</c> to a directory and these run, otherwise they skip.
/// <para>
/// Not tests: they assert nothing, because what they are for is the part §4.3 says cannot be asserted. They
/// exist because "it builds and the tests pass" is no answer to a question about layout, and a picture is —
/// the stacked-results floor (#81) was wrong in a way only the capture showed.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class LookProbe
{
    private readonly UiTestSession _ui;
    private readonly ITestOutputHelper _out;

    public LookProbe(UiTestSession ui, ITestOutputHelper output)
    {
        _ui = ui;
        _out = output;
    }

    /// <summary>The directory to write into, or a skip when nobody asked for captures.</summary>
    private static string CaptureDir()
    {
        var dir = Environment.GetEnvironmentVariable("BEARING_UI_CAPTURE_DIR");
        Skip.If(string.IsNullOrWhiteSpace(dir), "set BEARING_UI_CAPTURE_DIR to render UI captures");
        Directory.CreateDirectory(dir!);
        return dir!;
    }

    /// <summary>Result sets of very different sizes — what the stack has to divide sensibly (#81).</summary>
    [SkippableFact]
    public Task StackedResults()
    {
        var dir = CaptureDir();
        return _ui.Run(() =>
        {
            var (window, _) = ResultsHarness.Show(Set("stores", 3), Set("payments", 240), Set("audit", 12));
            Write(window, dir, "stacked-results.png");
            window.Close();
        });
    }

    /// <summary>A whole demo run (#63): every result shape the view has to lay out, off the fixtures.</summary>
    [SkippableFact]
    public Task DemoRun()
    {
        var dir = CaptureDir();
        return _ui.Run(() =>
        {
            var sets = DemoRenderTests.Sets("select * from shop.store", [.. Demo.DemoData.Run()]);
            var (window, _) = ResultsHarness.Show([.. sets]);
            Write(window, dir, "demo-run.png");
            window.Close();
        });
    }

    /// <summary>The pinned row, selected and not — the two-row selection look (#67).</summary>
    [SkippableFact]
    public Task PinnedTabs()
    {
        var dir = CaptureDir();
        return _ui.Run(async () =>
        {
            using var shell = await ShellHarness.ShowAsync(nameof(PinnedTabs));
            var workspace = shell.Vm.Workspace;
            workspace.Tabs.Clear();
            var daily = workspace.NewTab("-- daily revenue");
            daily.DisplayName = "daily-revenue";
            var audit = workspace.NewTab("-- audit");
            audit.DisplayName = "audit-trail";
            foreach (var name in new[] { "scratch-1", "scratch-2", "scratch-3" })
                workspace.NewTab("-- x").DisplayName = name;

            workspace.SetPinned(daily, true);
            workspace.SetPinned(audit, true);
            workspace.SelectedTab = daily;
            shell.Pump();
            Write(shell.Window, dir, "pinned-selected.png");

            // The other row must stop drawing its own selection, which it cannot clear (a TabStrip is
            // always-selected) — so the `dormant` class is what has to do it.
            workspace.SelectedTab = workspace.UnpinnedTabs[1];
            shell.Pump();
            Write(shell.Window, dir, "pinned-unselected.png");
        });
    }

    private void Write(Avalonia.Controls.Window window, string dir, string name)
    {
        var path = Path.Combine(dir, name);
        FrameCapture.Dump(window, path);
        _out.WriteLine($"wrote {path}");
    }

    private static ResultSetViewModel Set(string column, int rows)
    {
        var data = Enumerable.Range(1, rows).Select(i => new object?[] { i, $"{column}-{i}" }).ToList();
        var result = new QueryResult(
            [new ColumnDescriptor("id", "int4", typeof(int)), new ColumnDescriptor(column, "text", typeof(string))],
            data, data.Count, TimeSpan.FromMilliseconds(9), null, null, false);
        return new ResultSetViewModel(result, $"select * from {column}", pageable: false);
    }
}
