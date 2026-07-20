namespace Squirrel.Persistence;

/// <summary>XDG-aware locations for app-global state (recent projects, query log, fallback secrets).</summary>
public static class SquirrelPaths
{
    /// <summary>
    /// The per-user app directory name. Defaults to <c>squirrel</c>; when <c>SQUIRREL_PROFILE</c> is set
    /// it becomes <c>squirrel-&lt;profile&gt;</c>, giving a fully isolated config/data/secrets namespace.
    /// Used by dev builds (see Squirrel.Desktop launchSettings.json) so running from source never touches
    /// the installed app's real projects and settings.
    /// </summary>
    public static string AppDirName
    {
        get
        {
            var profile = Environment.GetEnvironmentVariable("SQUIRREL_PROFILE");
            if (string.IsNullOrWhiteSpace(profile)) return "squirrel";
            // Keep it filesystem/keychain safe: letters, digits, dash, underscore.
            var clean = new string(profile.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
            return $"squirrel-{clean}";
        }
    }

    public static string ConfigDir =>
        EnsureDir(Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Home, ".config"),
            AppDirName));

    public static string DataDir =>
        EnsureDir(Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(Home, ".local", "share"),
            AppDirName));

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
