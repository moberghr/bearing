using System;
using System.Collections.Generic;
using System.IO;

namespace Bearing.App.Workspace;

/// <summary>
/// The scripts a launch asked the window to open: <c>bearing-app file.sql</c>, which is what the
/// <c>bearing</c> command starts when no window answered it, what a file association starts, and — inside
/// the Linux AppImage — what <c>bearing file.sql</c> reaches directly.
/// <para>
/// Pure, and the same rule the command uses (<c>CliParser.IsScriptPath</c>; the app may not reference the
/// command): an argument ending in <c>.sql</c> is a script, anything else — <c>--demo</c> — is not this
/// class's business.
/// </para>
/// </summary>
public static class StartupScripts
{
    /// <summary>Absolute paths, against <paramref name="workingDirectory"/> (this process's when null).</summary>
    public static IReadOnlyList<string> From(IReadOnlyList<string>? args, string? workingDirectory = null)
    {
        var scripts = new List<string>();
        if (args is null) return scripts;

        foreach (var arg in args)
        {
            if (arg.StartsWith('-') || !arg.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                scripts.Add(workingDirectory is null ? Path.GetFullPath(arg) : Path.GetFullPath(arg, workingDirectory));
            }
            catch { /* not a usable path; nothing a launch can say about it */ }
        }
        return scripts;
    }
}
