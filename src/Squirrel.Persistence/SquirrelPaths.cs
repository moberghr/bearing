namespace Squirrel.Persistence;

/// <summary>XDG-aware locations for app-global state (recent projects, query log, fallback secrets).</summary>
public static class SquirrelPaths
{
    public static string ConfigDir =>
        EnsureDir(Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Home, ".config"),
            "squirrel"));

    public static string DataDir =>
        EnsureDir(Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(Home, ".local", "share"),
            "squirrel"));

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
