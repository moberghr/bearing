using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Squirrel.App.ViewModels;
using Squirrel.App.Views;
using Squirrel.Core.Logging;
using Squirrel.Core.Workspace;
using Squirrel.Data;
using Squirrel.Persistence;

namespace Squirrel.App;

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
            var providers = new ProviderRegistry();
            IProjectStore projectStore = new JsonProjectStore();
            ISessionStore sessionStore = new JsonSessionStore();
            var settings = new AppSettingsStore().Load();
            IQueryLog queryLog = new SqliteQueryLog(retentionDays: settings.QueryLogRetentionDays);
            IRecentProjects recentProjects = new FileRecentProjects();

            var vm = new MainWindowViewModel(providers, projectStore, sessionStore, queryLog, recentProjects);
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

            // Optional startup timing (set SQUIRREL_STARTUP_TIMING=1) — measures process start → first
            // window shown, and → project/demo restore complete (the off-thread work).
            if (Environment.GetEnvironmentVariable("SQUIRREL_STARTUP_TIMING") is { Length: > 0 })
                window.Opened += (_, _) => LogStartup("window shown");

            // Resolve the keychain and restore the project OFF the UI thread — never block startup.
            _ = InitializeAsync(vm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeAsync(MainWindowViewModel vm)
    {
        var secretStore = await SecretStoreFactory.CreateAsync();
        vm.AttachSecretStore(secretStore);
        await vm.InitializeAsync(DefaultProjectDirectory());
        // First-run convenience: point the default project at the local pagila demo container.
        await vm.SeedDemoConnectionAsync("localhost", 5434, "pagila", "postgres", "squirrel");
        LogStartup("project + demo ready");
    }

    /// <summary>Write a startup milestone (ms since process start) when SQUIRREL_STARTUP_TIMING is set.</summary>
    internal static void LogStartup(string milestone)
    {
        if (Environment.GetEnvironmentVariable("SQUIRREL_STARTUP_TIMING") is not { Length: > 0 }) return;
        try
        {
            var since = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            Console.Error.WriteLine($"[startup] {milestone}: {since.TotalMilliseconds:F0} ms");
        }
        catch { /* best-effort diagnostic */ }
    }

    private static string DefaultProjectDirectory()
        => Path.Combine(SquirrelPaths.DataDir, "projects", "default");
}
