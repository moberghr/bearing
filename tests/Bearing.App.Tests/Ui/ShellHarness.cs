using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The whole shell — <see cref="MainWindow"/> with a real <see cref="ShellViewModel"/> — on a headless
/// window. It constructs and shows without a display server, which makes the wiring that lives only in the
/// window assertable: keyboard focus, the tab strip's two-way selection binding, the key routing.
/// <para>
/// Use it for claims that only hold with the view attached. #87 is the example: closing a tab picked the
/// wrong one <i>because</i> the bound strip writes its own selection back mid-removal, so every view-model
/// test agreed with the broken code. Prefer <see cref="ResultsHarness"/> or a plain unit test where the
/// window is not the point — this builds a query log and a project directory per instance.
/// </para>
/// <para>
/// This needed two things fixed before it could exist, both of them real bugs rather than test scaffolding:
/// shared brushes are immutable now (a mutable one takes the dispatcher of whichever thread built it, so a
/// static cache filled on an xunit worker thread threw <c>VerifyAccess</c> from the compositor once a
/// visual here used it), and the token caches are keyed per <see cref="Avalonia.Application"/> (an
/// unconditional static cache let whichever test ran first decide the value for every later one).
/// </para>
/// </summary>
internal sealed class ShellHarness : IDisposable
{
    private readonly string _root;

    private ShellHarness(string root, MainWindow window, ShellViewModel vm)
    {
        _root = root;
        Window = window;
        Vm = vm;
    }

    public MainWindow Window { get; }
    public ShellViewModel Vm { get; }

    /// <summary>Build the shell over a fresh temporary project and show it.</summary>
    public static async Task<ShellHarness> ShowAsync(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "bearing-shell", Guid.NewGuid().ToString("N"));
        var vm = new ShellViewModel(
            new ProviderRegistry(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(root, "recent.json")),
            dialogs: new FakeDialogs(),
            // Autosave off: these tests are about the window, and a background writer racing the temp
            // directory they delete on the way out is noise they don't need.
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        await vm.InitializeAsync(Path.Combine(root, name));

        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        window.Show();
        var harness = new ShellHarness(root, window, vm);
        harness.Pump();
        return harness;
    }

    /// <summary>Lay out and drain the dispatcher. The window posts work at
    /// <see cref="DispatcherPriority.Loaded"/> (focus, scroll-into-view), so a layout pass alone is not
    /// enough to see the result of an action.</summary>
    public void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        }
    }

    public void Dispose()
    {
        try { Window.Close(); } catch { /* already closing */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* temp dir */ }
    }
}
