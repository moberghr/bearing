using System.Runtime.InteropServices;

namespace Bearing.Persistence.Import;

/// <summary>A DBeaver project found on this machine, and the file to import from it.</summary>
public sealed record DBeaverProject(string Name, string DataSourcesPath)
{
    /// <summary>What the picker shows: the project name plus the workspace it lives in, since a machine can
    /// hold several workspaces whose projects are all called "General".</summary>
    public string Label
    {
        get
        {
            var workspace = Directory.GetParent(Path.GetDirectoryName(DataSourcesPath)!)?.Parent?.Name;
            return workspace is null ? Name : $"{Name} ({workspace})";
        }
    }
}

/// <summary>
/// Finds DBeaver workspaces on this machine (#72). Probing is a convenience, never the contract: the
/// workspace location is user-configurable and DBeaver Enterprise/Lite use different data directories, so
/// the import must always also accept a file the user picks by hand.
/// </summary>
public static class DBeaverWorkspaces
{
    /// <summary>The per-project file that holds connections, relative to a project directory.</summary>
    public const string DataSourcesFile = "data-sources.json";

    private const string ProjectMetadataDir = ".dbeaver";

    /// <summary>
    /// Every project with a <c>data-sources.json</c> under the default DBeaver data directories, newest
    /// first so the workspace someone actually uses is offered before one they abandoned. Returns empty
    /// rather than throwing when DBeaver isn't installed, or when a directory can't be read.
    /// </summary>
    public static IReadOnlyList<DBeaverProject> Discover()
    {
        var found = new List<(DBeaverProject Project, DateTime Written)>();
        foreach (var root in DataRoots())
        {
            foreach (var workspace in Subdirectories(root))
            {
                // Workspaces are named workspace6, workspace7, … — enumerated rather than pinned to a
                // version so a DBeaver upgrade doesn't quietly make the import stop finding anything.
                foreach (var project in Subdirectories(workspace))
                {
                    var path = Path.Combine(project, ProjectMetadataDir, DataSourcesFile);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        found.Add((new DBeaverProject(new DirectoryInfo(project).Name, path),
                                   File.GetLastWriteTimeUtc(path)));
                    }
                    catch (IOException) { /* vanished or locked between the probe and the stat */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        return found.OrderByDescending(f => f.Written).Select(f => f.Project).ToList();
    }

    /// <summary>Where DBeaver keeps its workspaces per platform. Both plausible Linux locations are probed:
    /// the XDG data directory is current, the home-directory one is what older installs used.</summary>
    private static IEnumerable<string> DataRoots()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (appData.Length > 0) yield return Path.Combine(appData, "DBeaverData");
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) yield break;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.Combine(home, "Library", "DBeaverData");
            yield break;
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        yield return Path.Combine(
            string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg, "DBeaverData");
        yield return Path.Combine(home, "DBeaverData");
    }

    private static IEnumerable<string> Subdirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
