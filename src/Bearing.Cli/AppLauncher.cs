using System.Diagnostics;
using Bearing.Persistence;

namespace Bearing.Cli;

/// <summary>
/// Opens the Bearing window. <c>bearing</c> with no arguments at all does this, so the name a person knows
/// still does the thing they expect — the command surface is what the <i>arguments</i> select, not what the
/// name means.
/// </summary>
public interface IAppLauncher
{
    /// <summary>Launch it, or say why not. Null means it started; the command returns immediately either
    /// way, because a shell that blocks until a window closes is not what typing an app's name does.</summary>
    string? Launch();

    /// <summary>
    /// Open these scripts in Bearing: in the window that is already open if there is one — its current
    /// project — and otherwise in a new window, which opens them in the last project used. Null means they
    /// were handed over; like <see cref="Launch"/>, nothing waits for the window.
    /// </summary>
    Task<string?> OpenAsync(IReadOnlyList<string> files);
}

/// <summary>
/// Finds the GUI beside this executable and starts it.
/// <para>
/// The two are always siblings — <c>build/velopack.sh</c> publishes both into one directory precisely so
/// that they cannot drift apart or be installed separately — so "beside me" is a stronger answer than a
/// search path or a registry lookup, and it keeps a copy of Bearing run from a folder working the same way
/// as an installed one.
/// </para>
/// </summary>
public sealed class AppLauncher : IAppLauncher
{
    /// <summary>The GUI apphost's file name. Not <c>bearing</c>: that is this command (see
    /// <c>Bearing.Desktop.csproj</c> for why round that way).</summary>
    private const string GuiName = "bearing-app";

    public string? Launch() => Launch([]);

    public async Task<string?> OpenAsync(IReadOnlyList<string> files)
    {
        // Absolute before anything leaves this process: the window has its own working directory, and on
        // macOS `open` starts it in `/`, so a relative path would name a different file there.
        var paths = new List<string>();
        foreach (var file in files)
        {
            string path;
            try { path = Path.GetFullPath(file); }
            catch { return $"'{file}' is not a usable path."; }

            // Checked here rather than left to the window, which can only say so in a status bar the person
            // who typed the command may not be looking at.
            if (!File.Exists(path)) return $"No such file: {path}";
            paths.Add(path);
        }

        if (await ScriptOpenChannel.TrySendAsync(paths).ConfigureAwait(false)) return null;
        return Launch(paths);
    }

    private static string? Launch(IReadOnlyList<string> arguments)
    {
        var directory = AppContext.BaseDirectory;

        // macOS: open the *bundle*, never the executable inside it. Launch Services is what gives the app
        // its Dock entry, its icon and its activation; a bundled binary started directly gets none of them
        // and comes up behind whatever window is in front.
        if (OperatingSystem.IsMacOS() && BundleAbove(directory) is { } bundle)
            return Start("open", arguments.Count == 0 ? ["-a", bundle] : ["-a", bundle, "--args", .. arguments]);

        var gui = Path.Combine(directory, OperatingSystem.IsWindows() ? GuiName + ".exe" : GuiName);
        if (!File.Exists(gui))
            return $"Could not find the Bearing app beside this command (looked for {gui}).";

        return Start(gui, arguments);
    }

    /// <summary>The <c>.app</c> this executable lives in, or null when it does not live in one — which is
    /// the case for a build directory on a developer's Mac, and is why this is a search rather than three
    /// fixed levels up.</summary>
    private static string? BundleAbove(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return current.FullName;

        return null;
    }

    private static string? Start(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo(fileName) { UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            // The window outlives this process, so nothing is awaited and the exit code is about whether it
            // *started*. A GUI that then fails reports that in its own window, where someone is looking.
            using var process = Process.Start(start);
            return process is null ? $"Could not start {fileName}." : null;
        }
        catch (Exception ex)
        {
            return $"Could not start {fileName}: {ex.Message}";
        }
    }
}
