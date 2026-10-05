using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Bearing.Updates;

/// <summary>
/// Makes the Linux AppImage answer to <c>bearing &lt;command&gt;</c> the way the installed <c>bearing</c>
/// does on Windows and macOS.
/// <para>
/// <b>Why this exists at all.</b> An AppImage is one file with one entry point: its <c>AppRun</c> reads the
/// <c>Exec=</c> line of the desktop entry Velopack writes — <c>bearing-app</c>, the <c>--mainExe</c> — and
/// runs it with every argument. The <c>bearing</c> command is inside the image beside it (§1.11) and
/// nothing outside can name it, so the obvious install — <c>BearingSql.AppImage</c> symlinked onto
/// <c>PATH</c> as <c>bearing</c> — opened a window for <c>bearing query …</c> and dropped the arguments.
/// It worked on Windows because the installer puts the directory holding both executables on <c>PATH</c>.
/// </para>
/// <para>
/// So inside an AppImage the window's entry point does the routing that the command's does elsewhere,
/// mirrored: no arguments is the window, anything else is the command. Making the command the
/// <c>--mainExe</c> instead was the alternative and is worse — <c>vpk pack</c> requires the Velopack hooks in
/// the entry assembly, and the update restart and the desktop launcher would then start a console program
/// to start the window.
/// </para>
/// </summary>
public static class AppImageCommand
{
    /// <summary>The command's file name inside the image, beside <c>bearing-app</c>.</summary>
    public const string CommandName = "bearing";

    /// <summary>
    /// Whether these arguments are for the command rather than the window.
    /// <para>
    /// Only inside an AppImage (<paramref name="appImage"/> is the <c>APPIMAGE</c> variable its runtime
    /// sets): a <c>bearing-app</c> started from an install directory or a build has the command beside it
    /// under its own name, and forwarding there would only hide which one ran.
    /// </para>
    /// <para>
    /// <paramref name="appArguments"/> are the switches the window itself takes (<c>--demo</c>). A launch
    /// made only of those stays a window, so <c>BearingSql.AppImage --demo</c> keeps working; one that mixes
    /// them with anything else goes to the command, which says what it does not understand — a window that
    /// silently ignored the rest is the bug this fixes.
    /// </para>
    /// </summary>
    public static bool ShouldForward(
        IReadOnlyList<string> args, string? appImage, IReadOnlyCollection<string> appArguments)
    {
        if (string.IsNullOrEmpty(appImage) || args.Count == 0) return false;
        return !args.All(a => appArguments.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Run the command beside this executable with <paramref name="args"/> and return its exit code.
    /// <para>
    /// Not an <c>exec</c> — .NET has none — so this process waits as the command's parent, and it has to:
    /// the AppImage runtime unmounts the image when its child (this process) exits, which would pull the
    /// command's own files out from under it. For the same reason a Ctrl+C is left to the command alone.
    /// The terminal delivers SIGINT to the whole foreground group, and this process's default answer would
    /// be to exit first; the command cancels its statement and returns, and this one returns with it.
    /// </para>
    /// <para>
    /// Standard input, output and error are inherited rather than redirected, so pipes, <c>--file -</c>
    /// and a terminal's width reach the command exactly as they would have reached it directly.
    /// </para>
    /// </summary>
    public static int Forward(IReadOnlyList<string> args, TextWriter error)
    {
        var command = Path.Combine(AppContext.BaseDirectory, CommandName);
        if (!File.Exists(command))
        {
            error.WriteLine($"This Bearing package has no '{CommandName}' command (looked for {command}).");
            return 1;
        }

        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => c.Cancel = true);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, c => c.Cancel = true);

        try
        {
            var start = new ProcessStartInfo(command) { UseShellExecute = false };
            foreach (var argument in args) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null)
            {
                error.WriteLine($"Could not start {command}.");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not start {command}: {ex.Message}");
            return 1;
        }
    }
}
