using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Bearing.Updates;

/// <summary>
/// Gives the terminal back when the Linux AppImage is started from a shell to become a window —
/// <c>bearing</c> and <c>bearing file.sql</c> through the usual <c>~/.local/bin/bearing</c> symlink.
/// <para>
/// <b>Why it blocked.</b> Elsewhere the <c>bearing</c> command starts <c>bearing-app</c> and exits. Inside an
/// AppImage there is one entry point, and the window is that process (see <see cref="AppImageCommand"/>): the
/// shell waits for the image's runtime, the runtime waits for its child, and the child is the window — so the
/// prompt came back only when the window closed. The runtime cannot simply be left behind either: it unmounts
/// the image when its child exits.
/// </para>
/// <para>
/// <b>So the image starts itself again, detached</b> — a new session (<c>setsid</c>), standard streams on
/// <c>/dev/null</c>, in the background — and this process exits at once. The second image mounts its own copy
/// and becomes the window. It costs one extra mount, which is why this happens only when a terminal is
/// attached: the desktop launcher and the update restart start the image with no terminal, and they become
/// the window directly, as before.
/// </para>
/// </summary>
public static class AppImageDetach
{
    /// <summary>Set on the relaunched image so it never relaunches again, whatever its streams look like.</summary>
    public const string Marker = "BEARING_DETACHED";

    /// <summary>
    /// Pure: whether this launch should relaunch itself in the background. Only inside an AppImage
    /// (<paramref name="appImage"/>, the runtime's <c>APPIMAGE</c>), only from a terminal, and only once.
    /// </summary>
    public static bool ShouldDetach(string? appImage, bool terminalAttached, string? marker)
        => !string.IsNullOrEmpty(appImage) && terminalAttached && string.IsNullOrEmpty(marker);

    /// <summary>Whether any of the three standard streams is a terminal — a shell started this.</summary>
    public static bool TerminalAttached()
        => !Console.IsInputRedirected || !Console.IsOutputRedirected || !Console.IsErrorRedirected;

    /// <summary>
    /// The shell line that starts <c>"$@"</c> detached. <c>setsid</c> when there is one (util-linux, so
    /// practically always), so closing the terminal later does not take the window with it; plain
    /// backgrounding otherwise, which still returns the prompt.
    /// </summary>
    internal const string Script =
        "if command -v setsid >/dev/null 2>&1; then setsid \"$@\" </dev/null >/dev/null 2>&1 & "
        + "else \"$@\" </dev/null >/dev/null 2>&1 & fi";

    /// <summary>
    /// Start <paramref name="appImage"/> again with <paramref name="args"/>, detached. True when it was
    /// handed off and this process should exit; false (with the reason on <paramref name="error"/>) means
    /// nothing started, and the caller becomes the window in the foreground as it always did.
    /// </summary>
    public static bool Relaunch(string appImage, IReadOnlyList<string> args, TextWriter error)
    {
        try
        {
            var start = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(Script);
            start.ArgumentList.Add("sh");   // $0
            start.ArgumentList.Add(appImage);
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.Environment[Marker] = "1";

            using var shell = Process.Start(start);
            if (shell is null) return false;
            // The shell only forks and returns; waiting for it is what makes the hand-off certain before
            // this process — and with it this mount of the image — goes away.
            shell.WaitForExit();
            return shell.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not start Bearing in the background ({ex.Message}); opening it here.");
            return false;
        }
    }
}
