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
    public static string? Start()
    {
        // ProcessPath, not Assembly.Location: under a single-file or Velopack layout the assembly path is
        // not the executable, and it is the executable that has to be re-run.
        if (Environment.ProcessPath is not { Length: > 0 } exe)
            return "Couldn't find the application executable to start a demo session.";

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add(DemoMode.Argument);
            return Process.Start(start) is null ? "Couldn't start a demo session." : null;
        }
        catch (Exception ex)
        {
            return $"Couldn't start a demo session: {ex.Message}";
        }
    }
}
