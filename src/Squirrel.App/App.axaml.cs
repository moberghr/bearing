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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var providers = new ProviderRegistry();
            IProjectStore projectStore = new JsonProjectStore();
            ISessionStore sessionStore = new JsonSessionStore();
            IQueryLog queryLog = new SqliteQueryLog();
            IRecentProjects recentProjects = new FileRecentProjects();

            var vm = new MainWindowViewModel(providers, projectStore, sessionStore, queryLog, recentProjects);
            var window = new MainWindow { DataContext = vm };

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
    }

    private static string DefaultProjectDirectory()
        => Path.Combine(SquirrelPaths.DataDir, "projects", "default");
}
