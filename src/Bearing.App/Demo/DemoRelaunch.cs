using System;
using System.Diagnostics;
using Bearing.Demo;

namespace Bearing.App.Demo;

/// <summary>
/// Starts a second copy of the app in demo mode (#64).
/// <para>
/// A relaunch rather than a switch, because demo mode is decided once at startup: it replaces the provider
/// registry, all four stores and the secret store, and swapping those under a running window would leave a
/// session half demo and half real — which is precisely the confusion the isolation exists to prevent. The
/// price is a new process; the alternative was a fake provider reachable from an ordinary session.
/// </para>
/// <para>
/// The current window is <b>not</b> closed. The user's real session stays exactly as it was, which is what
/// makes trying the demo a safe thing to click: their unsaved buffers are not part of the bargain, and there
/// is nothing to restore afterwards because the demo leaves nothing behind.
/// </para>
/// </summary>
public static class DemoRelaunch
{
    /// <summary>
    /// Launch a demo session. Returns null on success, or a short reason to show in the status bar —
    /// failing to start a second process is a message, not a crash (§5.2).
    /// </summary>
    public static string? Start() => Start(Environment.ProcessPath, Launch);

    /// <summary>
    /// The testable shape: the executable and the launcher are arguments.
    /// <para>
    /// Split out because the only thing worth asserting here is the decision-making — a missing executable, a
    /// launcher that fails, the argument actually passed — and a test that verified any of it by starting a
    /// second copy of the app would be a test that starts a second copy of the app.
    /// </para>
    /// </summary>
    /// <param name="executablePath">
    /// <c>Environment.ProcessPath</c> in the app. Not <c>Assembly.Location</c>: under a single-file or
    /// Velopack layout that is not the executable, and it is the executable that has to be re-run.
    /// </param>
    /// <param name="launch">Starts the process, returning false when it could not. Throwing is allowed.</param>
    internal static string? Start(string? executablePath, Func<string, string, bool> launch)
    {
        if (executablePath is not { Length: > 0 } exe)
            return "Couldn't find the application executable to start a demo session.";

        try
        {
            return launch(exe, DemoMode.Argument) ? null : "Couldn't start a demo session.";
        }
        catch (Exception ex)
        {
            return $"Couldn't start a demo session: {ex.Message}";
        }
    }

    private static bool Launch(string exe, string argument)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add(argument);
        return Process.Start(start) is not null;
    }
}
