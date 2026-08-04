using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;

namespace Bearing.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Recolor the app-wide <c>ConnectionBrush</c> from the active connection's environment hex.
    /// Mutating the shared brush's color updates every <c>{DynamicResource ConnectionBrush}</c>
    /// consumer at once (tab accent, dots, results accent, status-bar line). Null/invalid → neutral.
    /// </summary>
    public static void SetConnectionAccent(string? environmentHex)
    {
        if (Current?.Resources.TryGetResource("ConnectionBrush", Current.ActualThemeVariant, out var res) == true
            && res is Avalonia.Media.SolidColorBrush brush)
        {
            brush.Color = Theming.ConnectionColors.Resolve(environmentHex);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LogStartup("framework init done");

            // Resilience: catch UI-thread exceptions (including those escaping async void handlers),
            // log + show them, and keep the app alive. AppDomain/TaskScheduler backstops live in
            // Program.Main. Surface presents the error dialog on the UI thread, owned by the main window.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                CrashReporter.Report("UI thread", e.Exception);
                e.Handled = true;
            };
            CrashReporter.Surface = (context, ex) =>
                Dispatcher.UIThread.Post(() => Views.ErrorDialog.Show(desktop.MainWindow, context, ex));

            var providers = new ProviderRegistry();
            IProjectStore projectStore = new JsonProjectStore();
            ISessionStore sessionStore = new JsonSessionStore();
            var settings = new AppSettingsStore().Load();
            IQueryLog queryLog = new SqliteQueryLog(retentionDays: settings.QueryLogRetentionDays);
            IRecentProjects recentProjects = new FileRecentProjects();

            var vm = new ShellViewModel(providers, projectStore, sessionStore, queryLog, recentProjects,
                dialogs: new Views.DialogService(),
                credentialPrompt: new Views.DialogCredentialPrompt());
            LogStartup("vm created");
            var window = new MainWindow { DataContext = vm };
            LogStartup("window constructed");

            // Persist the session on every exit path, exactly once. A window close fires Closing on
            // the UI thread; a killed process (Ctrl+C in the terminal, IDE stop) shuts the runtime
            // down and runs ProcessExit instead. Save is synchronous (no deadlock) and best-effort;
            // the live editor's text is already mirrored into the tab, so the off-UI-thread
            // ProcessExit path skips FlushActiveEditor (which would touch controls off-thread).
            var saved = 0;
            void SaveSession(bool fromUiThread)
            {
                if (Interlocked.Exchange(ref saved, 1) != 0) return;
                if (fromUiThread) window.FlushActiveEditor();
                vm.SaveWorkspace();
                _ = vm.DisposeSessionsAsync();
            }

            window.Closing += (_, _) => SaveSession(fromUiThread: true);
            desktop.ShutdownRequested += (_, _) => SaveSession(fromUiThread: true);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveSession(fromUiThread: false);

            desktop.MainWindow = window;

            // Optional startup timing (set BEARING_STARTUP_TIMING=1) — measures process start → first
            // window shown, and → project/demo restore complete (the off-thread work).
            if (Environment.GetEnvironmentVariable("BEARING_STARTUP_TIMING") is { Length: > 0 })
                window.Opened += (_, _) => LogStartup("window shown");

            // Resolve the keychain and restore the project OFF the UI thread — never block startup.
            // Observed so a failure to restore is logged + surfaced rather than lost.
            CrashReporter.Observe(InitializeAsync(vm), "startup initialize");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeAsync(ShellViewModel vm)
    {
        var secretStore = await SecretStoreFactory.CreateAsync();
        vm.AttachSecretStore(secretStore);
        // Reopen the last-used project; fall back to the default project on first run (or if it's gone).
        await vm.ResumeLastProjectAsync(DefaultProjectDirectory());
        // Opt-in convenience: seed the local pagila demo connection only when BEARING_SEED_DEMO is set.
        // By default a fresh profile starts as an empty project — no connections, no history.
        if (Environment.GetEnvironmentVariable("BEARING_SEED_DEMO") is { Length: > 0 })
            await vm.Connections.SeedDemoConnectionAsync("localhost", 5434, "pagila", "postgres", "squirrel");
        LogStartup("project ready");
    }

    /// <summary>Write a startup milestone (ms since process start) when BEARING_STARTUP_TIMING is set.</summary>
    internal static void LogStartup(string milestone)
    {
        if (Environment.GetEnvironmentVariable("BEARING_STARTUP_TIMING") is not { Length: > 0 }) return;
        try
        {
            var since = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            Console.Error.WriteLine($"[startup] {milestone}: {since.TotalMilliseconds:F0} ms");
        }
        catch { /* best-effort diagnostic */ }
    }

    private static string DefaultProjectDirectory()
        => Path.Combine(BearingPaths.DataDir, "projects", "default");
}
