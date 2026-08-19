using System;
using System.Threading.Tasks;
using Avalonia;
using Bearing.App;
using Bearing.Persistence;
using Velopack;

namespace Bearing.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Updater hooks first, before anything else — including the backstops below. When the app is being
        // installed, updated or uninstalled this call exits the process from inside itself, so any code
        // above it would run on a machine mid-install. A no-op on a normal launch.
        //
        // Called inline rather than through a helper in Bearing.Updates on purpose: `vpk pack` verifies this
        // call is present in the entry assembly's Main and refuses to package without it, and that check is
        // worth keeping — an app that never runs its hooks installs and updates incorrectly. Everything else
        // about updating (the feed, UpdateManager) still lives behind IUpdateService in Bearing.Updates.
        VelopackApp.Build().Run();

        // Last-resort backstops so an escaped exception is recorded rather than lost. UI-thread faults
        // are handled (and surfaced) inside the app via Dispatcher.UnhandledException; these catch the
        // rest: background-thread crashes, unobserved task faults, and anything escaping startup.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write("AppDomain (fatal)", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Unobserved task", e.Exception);
            e.SetObserved(); // don't let an unobserved fire-and-forget fault tear the process down
        };

        try
        {
            AppBuilderFactory.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Startup (fatal)", ex);
            throw;
        }
    }
}
