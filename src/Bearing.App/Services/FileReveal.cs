using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Bearing.App.Services;

/// <summary>
/// Opens a file's containing folder in the OS file manager — the follow-up action on a finished export
/// ("where did it go?"). Best-effort: a machine with no file manager, or a sandbox that blocks launching
/// one, must not turn a successful export into an error.
/// </summary>
public static class FileReveal
{
    /// <summary>Show <paramref name="path"/> in the file manager, selecting it where the platform supports
    /// that. Returns false if nothing could be launched.</summary>
    public static bool OpenContainingFolder(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder is null || !Directory.Exists(folder)) return false;

            // Windows and macOS can highlight the file itself; on Linux there is no portable equivalent
            // (xdg-open takes a target, not a selection), so the folder is opened instead.
            var (exe, args) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ("explorer.exe", $"/select,\"{path}\"")
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? ("open", $"-R \"{path}\"")
                    : ("xdg-open", $"\"{folder}\"");

            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false; // no file manager, or launching one is not permitted here
        }
    }
}
