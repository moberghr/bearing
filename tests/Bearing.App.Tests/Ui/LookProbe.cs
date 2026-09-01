using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Bearing.App.Theming;
using Bearing.Core.Data;
using Bearing.Demo;
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
            var sets = DemoRenderTests.Sets("select * from shop.store", [.. DemoCatalog.Run()]);
            var (window, _) = ResultsHarness.Show([.. sets]);
            Write(window, dir, "demo-run.png");
            window.Close();
        });
    }

    /// <summary>A demo session's first screen (#64): the connection, the welcome script, the empty state gone.</summary>
    [SkippableFact]
    public Task DemoSession()
    {
        var dir = CaptureDir();
        return _ui.Run(async () =>
        {
            using var shell = await ShellHarness.ShowAsync(nameof(DemoSession));
            // The real entry point, so the capture shows what a demo launch actually produces.
            await shell.Vm.StartDemoAsync(shell.ProjectDirectory, DemoMode.WelcomeScript);
            shell.Pump();
            Write(shell.Window, dir, "demo-session.png");
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

    /// <summary>The schema tree, expanded onto a table's folders and its sizes (#46, #76, #71).</summary>
    [SkippableFact]
    public Task SchemaTree()
    {
        var dir = CaptureDir();
        return _ui.Run(async () =>
        {
            using var shell = await ShellHarness.ShowAsync(nameof(SchemaTree), new DemoProvider());
            await shell.Vm.StartDemoAsync(shell.ProjectDirectory, DemoMode.WelcomeScript);
            shell.Pump();

            // Server → database → payment, then its folders, which is the shape #46 added.
            var server = shell.Vm.Connections.ServerNodes.First();
            await Expand(server);
            var database = server.Children.First();
            await Expand(database);
            var payment = database.Children.First(n => n.Title.EndsWith("payment", StringComparison.Ordinal));
            await Expand(payment);
            foreach (var folder in payment.Children.Where(c => c.HasChildren || c.Children.Count > 0))
                folder.IsExpanded = true;
            shell.Pump();

            Write(shell.Window, dir, "schema-tree.png");
        });
    }

    /// <summary>Timestamps with their offsets, and a zone-less column badged (#77).</summary>
    [SkippableFact]
    public Task Timestamps()
    {
        var dir = CaptureDir();
        return _ui.Run(() =>
        {
            var (window, _) = ResultsHarness.Show(TimestampResult());
            Write(window, dir, "timestamps.png");
            window.Close();
        });
    }

    /// <summary>The same grid at two font sizes, which is what #52 made possible.</summary>
    [SkippableFact]
    public Task GridFontSize()
    {
        var dir = CaptureDir();
        return _ui.Run(() =>
        {
            Theming.FontScale.ApplyGrid(11);
            var (small, _) = ResultsHarness.Show(Set("payments", 12));
            Write(small, dir, "font-11.png");
            small.Close();

            Theming.FontScale.ApplyGrid(18);
            var (large, _) = ResultsHarness.Show(Set("payments", 12));
            Write(large, dir, "font-18.png");
            large.Close();

            Theming.FontScale.ApplyGrid(13);
        });
    }

    /// <summary>The connection dialog's encryption picker and what it warns about (#23).</summary>
    [SkippableFact]
    public Task ConnectionEncryption()
    {
        var dir = CaptureDir();
        return _ui.Run(() =>
        {
            var dialog = new Bearing.App.Views.ConnectionDialog(
                existing: null, existingPassword: null,
                test: (_, _, _) => Task.FromResult(false));
            dialog.Show();
            Pump(dialog);
            // A remote host, so the default is Require and the warning says what Require leaves open.
            if (dialog.FindControl<TextBox>("HostBox") is { } host) host.Text = "db.example.com";
            if (dialog.FindControl<TextBox>("DatabaseBox") is { } db) db.Text = "app";
            if (dialog.FindControl<TextBox>("UserBox") is { } user) user.Text = "reporting";
            Pump(dialog);

            Write(dialog, dir, "connection-tls.png");
            dialog.Close();
        });
    }

    /// <summary>The new settings rows: the two font dials, the display zone, and log redaction (#52, #77, #22).</summary>
    [SkippableFact]
    public Task Settings()
    {
        var dir = CaptureDir();
        return _ui.Run(async () =>
        {
            Bearing.Core.Workspace.SettingsCatalog.TimeZoneSuggestions = Bearing.App.Formatting.DisplayTimeZone.Available;
            Bearing.Core.Workspace.SettingsCatalog.TimeZoneValidator = Bearing.App.Formatting.DisplayTimeZone.IsKnown;
            Bearing.Core.Workspace.SettingsCatalog.TimeZoneDescriber = Bearing.App.Formatting.DisplayTimeZone.Describe;
            using var shell = await ShellHarness.ShowAsync(nameof(Settings));

            var settings = new Bearing.App.Views.SettingsWindow(shell.Vm.SettingsService);
            settings.Show();
            Pump(settings);

            // Searched rather than scrolled: the new rows are what this capture is for, and they sit below
            // the fold in a window that opens on "All".
            foreach (var (term, file) in new[]
                     {
                         ("font size", "settings-fonts.png"),
                         ("time zone", "settings-timezone.png"),
                         ("privacy", "settings-privacy.png"),
                     })
            {
                Search(settings, term);
                Pump(settings);
                Write(settings, dir, file);
            }
            settings.Close();
        });
    }

    /// <summary>A folded statement, and the gutter markers #74 was about.</summary>
    [SkippableFact]
    public Task FoldGutter()
    {
        var dir = CaptureDir();
        return _ui.Run(async () =>
        {
            using var shell = await ShellHarness.ShowAsync(nameof(FoldGutter));
            var editor = shell.Window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>()
                .First(e => e.Name == "Editor");
            editor.Text = """
                select p.id, p.store_id, p.amount, p.note
                from shop.payment p
                join shop.store s on s.id = p.store_id
                where p.amount > 10
                order by p.amount desc;

                select id, name, active
                from shop.store
                where active;
                """;
            shell.Pump();
            Write(shell.Window, dir, "fold-gutter.png");
        });
    }

    /// <summary>Many tabs, so the strip's overflow scroller shows (#65).</summary>
    [SkippableFact]
    public Task TabStripOverflow()
    {
        var dir = CaptureDir();
        return _ui.Run(async () =>
        {
            using var shell = await ShellHarness.ShowAsync(nameof(TabStripOverflow));
            shell.Vm.Workspace.Tabs.Clear();
            foreach (var name in new[]
                     {
                         "daily-revenue", "audit-trail", "store-health", "payment-recon", "customer-churn",
                         "index-bloat", "slow-queries", "scratch-1", "scratch-2", "scratch-3",
                     })
                shell.Vm.Workspace.NewTab($"-- {name}").DisplayName = name;
            shell.Vm.Workspace.SetPinned(shell.Vm.Workspace.Tabs[0], true);
            shell.Vm.Workspace.SelectedTab = shell.Vm.Workspace.Tabs[^1];
            shell.Pump();

            Write(shell.Window, dir, "tab-overflow.png");
        });
    }

    private static async Task Expand(Bearing.App.ViewModels.SchemaNodeViewModel node)
    {
        node.IsExpanded = true;
        await node.EnsureChildrenAsync();
    }

    /// <summary>Type into the settings window's search box, which is how its rows are filtered.</summary>
    private static void Search(Avalonia.Controls.Window settings, string term)
    {
        var box = settings.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.PlaceholderText?.StartsWith("Search", StringComparison.Ordinal) == true);
        box.Text = term;
    }

    private static void Pump(Avalonia.Controls.Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>A result with a timestamptz, a zone-less timestamp and a timetz side by side.</summary>
    private static ResultSetViewModel TimestampResult()
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int)),
            new ColumnDescriptor("paid_at", "timestamp with time zone", typeof(DateTime)),
            new ColumnDescriptor("booked_on", "timestamp without time zone", typeof(DateTime)),
            new ColumnDescriptor("cutoff", "time with time zone", typeof(DateTimeOffset)),
        };
        var rows = new List<object?[]>();
        for (var i = 1; i <= 6; i++)
            rows.Add([
                i,
                new DateTime(2026, 8, 26, 12 + i, 15, 0, DateTimeKind.Utc).AddTicks(3_729_580),
                new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Unspecified).AddHours(i),
                new DateTimeOffset(1, 1, 2, 17, 30, 0, TimeSpan.FromHours(2)),
            ]);
        var result = new QueryResult(columns, rows, rows.Count, TimeSpan.FromMilliseconds(12), null, null, false);
        return new ResultSetViewModel(result, "select id, paid_at, booked_on, cutoff from shop.payment",
            pageable: false);
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
