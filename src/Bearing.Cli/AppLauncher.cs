using System.Diagnostics;

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

    public string? Launch()
    {
        var directory = AppContext.BaseDirectory;

        // macOS: open the *bundle*, never the executable inside it. Launch Services is what gives the app
        // its Dock entry, its icon and its activation; a bundled binary started directly gets none of them
        // and comes up behind whatever window is in front.
        if (OperatingSystem.IsMacOS() && BundleAbove(directory) is { } bundle)
            return Start("open", ["-a", bundle]);

        var gui = Path.Combine(directory, OperatingSystem.IsWindows() ? GuiName + ".exe" : GuiName);
        if (!File.Exists(gui))
            return $"Could not find the Bearing app beside this command (looked for {gui}).";

        return Start(gui, []);
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

    private static string? Start(string fileName, string[] arguments)
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
