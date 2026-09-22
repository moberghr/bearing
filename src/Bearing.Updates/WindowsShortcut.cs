using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;

namespace Bearing.Updates;

/// <summary>
/// Repointing the Start Menu shortcut after 1.1.0 renamed the window's executable — the I/O half of
/// <see cref="WindowsShortcutTarget"/>, kept small because the deciding is over there.
/// <para>
/// A one-time migration, and it has to be ours: Velopack writes the shortcut when the app is installed and
/// never revisits it, and <c>Update.exe</c> has no command that would. A machine upgrading from 1.0.x
/// therefore keeps a Start Menu entry aimed at <c>current\bearing.exe</c>, which is the command now.
/// </para>
/// <para>
/// Best-effort throughout (§5.2), like the PATH edit beside it: a shortcut that cannot be rewritten costs a
/// console flash on launch, and an install that failed over one costs the install.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsShortcut
{
    /// <summary>
    /// Repoint every shortcut in the user's Start Menu and on their desktop that still names
    /// <paramref name="staleExe"/> inside <paramref name="appDirectory"/>.
    /// </summary>
    /// <returns>How many were rewritten — 0 on a machine that never had the old one, which is every fresh
    /// install and every update after the first.</returns>
    public static int Retarget(string appDirectory, string staleExe, string appExe)
    {
        var fixed_ = 0;

        foreach (var shortcut in Shortcuts())
        {
            try
            {
                if (Retarget(shortcut, appDirectory, staleExe, appExe)) fixed_++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable .lnk must not stop the others. A shortcut somebody else owns can be
                // locked, or point at something this process may not resolve.
            }
        }

        return fixed_;
    }

    /// <summary>The places a per-user install puts a shortcut. Machine-wide locations are deliberately not
    /// touched: Bearing installs per user without elevation, so anything there is not ours to edit.</summary>
    private static IEnumerable<string> Shortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            string[] found;
            try { found = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }

            foreach (var file in found) yield return file;
        }
    }

    /// <summary>
    /// Read one shortcut, and rewrite it only if <see cref="WindowsShortcutTarget.Corrected"/> says so.
    /// <para>
    /// Through <c>WScript.Shell</c> late-bound rather than a COM reference or a hand-rolled <c>.lnk</c>
    /// parser: the format is binary and versioned, and this needs to read one field and write one field on a
    /// path that must never throw. Reflection rather than <c>dynamic</c> so nothing depends on the C# binder
    /// being present.
    /// </para>
    /// </summary>
    /// <remarks>Internal so a test can drive one shortcut it made itself. The public entry above walks the
    /// user's <i>real</i> Start Menu, so nothing in a test suite may ever call it — a developer's own
    /// shortcuts are not a fixture.</remarks>
    internal static bool Retarget(string path, string appDirectory, string staleExe, string appExe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return false;

        object? shell = null;
        object? link = null;
        try
        {
            // Everything from here is COM and the filesystem, and both throw for reasons that are not this
            // migration's business: CreateShortcut refuses a path not ending .lnk or .url outright (measured
            // — it is a COMException, not a null), a .lnk can be corrupt, and one listed a moment ago can be
            // gone by the time it is opened. "Could not read it" is the same answer as "not ours", and this
            // runs where an exception is worse than doing nothing.
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            link = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            if (link is null) return false;

            var linkType = link.GetType();
            var target = linkType.InvokeMember(
                "TargetPath", BindingFlags.GetProperty, null, link, null) as string;

            if (WindowsShortcutTarget.Corrected(target, appDirectory, staleExe, appExe) is not { } corrected)
                return false;

            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [corrected]);
            // The working directory is the app's either way, but a shortcut whose two halves disagree is
            // the next person's puzzle.
            linkType.InvokeMember(
                "WorkingDirectory", BindingFlags.SetProperty, null, link, [appDirectory]);
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Release(link);
            Release(shell);
        }
    }

    /// <summary>Hand a COM object back. The installer callback's process is short-lived, but a held RCW on
    /// the shell can keep the Start Menu folder handle open, which is the sort of thing that makes the next
    /// step of an install fail for no visible reason.</summary>
    private static void Release(object? comObject)
    {
        if (comObject is null) return;
        try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(comObject); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort (§5.2) */ }
    }
}
