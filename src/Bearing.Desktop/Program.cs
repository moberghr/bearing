using System;
using System.Threading.Tasks;
using Avalonia;
using Bearing.App;
using System.IO;
using Bearing.Persistence;
using Bearing.Updates;
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
        // CA1416: the three Fast callbacks are annotated Windows-only. They are registered unconditionally
        // anyway, because they are *registrations* — on any other OS Velopack never invokes them, and the
        // handler itself returns immediately off Windows. Guarding the chain would mean splitting it, and
        // §9.6 pins it as one statement: `vpk pack` looks for it in the entry assembly and refuses to
        // package without it.
#pragma warning disable CA1416
        VelopackApp.Build()
            // Put the install directory on the user's PATH so `bearing` is a command, and take it off
            // again on uninstall (§1.11). These run inside the installer's own invocation of this exe, so
            // they have to be quick and must never throw — WindowsPath is best-effort throughout.
            //
            // Both install and update: an update lands in a new directory and swaps `current`, so the
            // entry is re-asserted rather than assumed. Add is idempotent, so the usual case writes
            // nothing and broadcasts nothing.
            .OnAfterInstallFastCallback(_ => OnInstalled())
            .OnAfterUpdateFastCallback(_ => OnInstalled())
            .OnBeforeUninstallFastCallback(_ => OnPath(add: false))
            .Run();
#pragma warning restore CA1416

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

    /// <summary>
    /// What an install or an update has to put right, in order. Both callbacks do the same work: an update
    /// lands in the same directory and swaps its contents, so everything here is written to be idempotent
    /// and to report "nothing to do" rather than rewriting what it agrees with.
    /// </summary>
    private static void OnInstalled()
    {
        if (!OperatingSystem.IsWindows()) return;

        OnPath(add: true);

        // A one-time repair for machines crossing 1.1.0. Velopack aims the Start Menu shortcut straight at
        // `current\<mainExe>` when the app is *installed* and never revisits it — Update.exe has no command
        // that would — so renaming the window's executable to make room for the `bearing` command (§1.11)
        // left every upgrading machine with a shortcut pointing at what is now the command. It still opens
        // Bearing, because the command with no arguments does exactly that, but through a console program,
        // so a console flashes on the way. A no-op on a fresh install and on every update after this one.
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/');
        WindowsShortcut.Retarget(directory, staleExe: "bearing.exe", appExe: "bearing-app.exe");
    }

    /// <summary>
    /// Add or remove the install directory on the user's PATH. Windows only — the macOS cask does this
    /// with a `binary` stanza, and where a Linux install puts things is the packager's business.
    /// </summary>
    private static void OnPath(bool add)
    {
        if (!OperatingSystem.IsWindows()) return;

        // Where this exe is: during an install callback that is the freshly installed directory, and it is
        // the same directory `bearing.exe` sits in, because build/velopack.sh publishes both into one.
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/');
        if (add) WindowsPath.Add(directory);
        else WindowsPath.Remove(directory);
    }
}
