using System;
using System.Threading.Tasks;
using Avalonia;
using Bearing.App;
using Bearing.Persistence;

namespace Bearing.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
