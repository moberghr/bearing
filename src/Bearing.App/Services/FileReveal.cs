using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bearing.App.Services;

/// <summary>
/// Shows a file in the OS file manager, <b>selected</b> where the platform can do that — the follow-up
/// action on a finished export ("where did it go?") and on a tab ("which file is this?"). Best-effort: a
/// machine with no file manager, or a sandbox that blocks launching one, must not turn a successful export
/// into an error.
/// </summary>
public static class FileReveal
{
    /// <summary>How long a reveal helper gets to report its exit code before we stop waiting and fall back.</summary>
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Show <paramref name="path"/> in the file manager with the file itself highlighted. Windows and macOS
    /// have a flag for it; on Linux the file managers implement the freedesktop
    /// <c>org.freedesktop.FileManager1.ShowItems</c> D-Bus method (Nautilus, Dolphin, Nemo, Thunar), which is
    /// tried first — <c>xdg-open</c> takes a target, not a selection, so it can only open the folder and is
    /// kept as the fallback for a desktop with no such service.
    /// </summary>
    /// <returns>False if nothing could be launched.</returns>
    public static async Task<bool> OpenContainingFolderAsync(string path)
    {
        string full, folder;
        try
        {
            full = Path.GetFullPath(path);
            folder = Path.GetDirectoryName(full) ?? "";
            if (folder.Length == 0 || !Directory.Exists(folder)) return false;
        }
        catch (Exception)
        {
            return false; // unusable path
        }

        // Explorer wants its selection as one raw, quoted command line — an escaped argument list is not
        // something it parses — so that one platform keeps the verbatim form.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Launch(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false });
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Launch(Args("open", "-R", full));

        // Linux: ask the session's file manager to select the item, and only fall back to opening the
        // folder if that didn't work (no such D-Bus service, or no gdbus to call it with).
        return await ShowItemAsync(full).ConfigureAwait(false) || Launch(Args("xdg-open", folder));
    }

    /// <summary>
    /// <c>org.freedesktop.FileManager1.ShowItems</c> via <c>gdbus</c>, awaited so a missing service is
    /// detected (it exits non-zero) rather than silently doing nothing. The URI goes in double quotes:
    /// <see cref="Uri.AbsoluteUri"/> percent-encodes a quote in a file name, so the GVariant literal can't
    /// be broken by one.
    /// </summary>
    private static async Task<bool> ShowItemAsync(string fullPath)
    {
        try
        {
            using var process = Process.Start(Args("gdbus",
                "call", "--session",
                "--dest", "org.freedesktop.FileManager1",
                "--object-path", "/org/freedesktop/FileManager1",
                "--method", "org.freedesktop.FileManager1.ShowItems",
                ShowItemsUris(fullPath), ""));
            if (process is null) return false;

            using var timeout = new CancellationTokenSource(LaunchTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false; // no gdbus, no session bus, or it never answered
        }
    }

    /// <summary>
    /// The <c>as</c> (array-of-string) argument for <c>ShowItems</c>, in GVariant text form. Pure, and worth
    /// pinning: a malformed literal doesn't error visibly — <c>gdbus</c> exits non-zero and the reveal quietly
    /// falls back to opening the folder, which is exactly the behaviour this replaced.
    /// <para>
    /// Double quotes, because <see cref="Uri.AbsoluteUri"/> percent-encodes a <c>"</c> in a file name but
    /// leaves an apostrophe alone — so the quote form that can't be broken by a legal file name is this one.
    /// </para>
    /// </summary>
    internal static string ShowItemsUris(string fullPath) => $"[\"{new Uri(fullPath).AbsoluteUri}\"]";

    /// <summary>A start descriptor with each argument passed separately, so the runtime quotes them and a
    /// space or a quote in a path can't turn into a second argument.</summary>
    private static ProcessStartInfo Args(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>Start a file manager and don't wait for it — there is no failure to detect beyond the launch
    /// itself once we're past the D-Bus attempt.</summary>
    private static bool Launch(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false; // no file manager, or launching one is not permitted here
        }
    }
}
