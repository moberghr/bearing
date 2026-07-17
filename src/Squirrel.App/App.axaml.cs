using System.IO;
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

            // Synchronous save — no async/await, so nothing to deadlock on during close.
            window.Closing += (_, _) =>
            {
                window.FlushActiveEditor();
                vm.SaveWorkspace();
            };

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
    }

    private static string DefaultProjectDirectory()
        => Path.Combine(SquirrelPaths.DataDir, "projects", "default");
}
