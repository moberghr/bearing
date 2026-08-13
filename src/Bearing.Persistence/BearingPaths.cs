namespace Bearing.Persistence;

/// <summary>
/// Per-user locations for app-global state (recent projects, query log, fallback secrets).
/// Follows each platform's own convention: XDG on Linux, <c>%APPDATA%</c>/<c>%LOCALAPPDATA%</c> on
/// Windows, <c>~/Library/Application Support</c> on macOS. The <c>XDG_*</c> environment variables are
/// honoured on every platform when set, so redirecting state (tests, portable installs) works uniformly.
/// </summary>
public static class BearingPaths
{
    /// <summary>
    /// The per-user app directory name. Defaults to <c>bearing</c>; when <c>BEARING_PROFILE</c> is set
    /// it becomes <c>bearing-&lt;profile&gt;</c>, giving a fully isolated config/data/secrets namespace.
    /// Used by dev builds (see Bearing.Desktop launchSettings.json) so running from source never touches
    /// the installed app's real projects and settings.
    /// </summary>
    public static string AppDirName
    {
        get
        {
            var profile = Environment.GetEnvironmentVariable("BEARING_PROFILE");
            if (string.IsNullOrWhiteSpace(profile)) return "bearing";
            // Keep it filesystem/keychain safe: letters, digits, dash, underscore.
            var clean = new string(profile.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
            return $"bearing-{clean}";
        }
    }

    /// <summary>Small, user-editable settings: recent projects, keybindings, app settings.</summary>
    public static string ConfigDir => EnsureDir(Path.Combine(ConfigRoot, AppDirName));

    /// <summary>Bulk/machine-local state: query log, fallback secrets, the default project.</summary>
    public static string DataDir => EnsureDir(Path.Combine(DataRoot, AppDirName));

    private static string ConfigRoot => ResolveRoot(
        PathKind.Config,
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        CurrentPlatform,
        Home,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string DataRoot => ResolveRoot(
        PathKind.Data,
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        CurrentPlatform,
        Home,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal enum PathKind { Config, Data }

    internal enum PathPlatform { Linux, Windows, MacOS }

    private static PathPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? PathPlatform.Windows
        : OperatingSystem.IsMacOS() ? PathPlatform.MacOS
        : PathPlatform.Linux;

    /// <summary>
    /// Pure root selection, kept separate from the environment so every platform's mapping is testable
    /// from any host. An explicit <c>XDG_*</c> value always wins — that is how tests, portable installs
    /// and <c>BEARING_PROFILE</c>-style isolation redirect state uniformly across platforms.
    /// </summary>
    internal static string ResolveRoot(
        PathKind kind, string? xdgOverride, PathPlatform platform,
        string home, string roamingAppData, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(xdgOverride)) return xdgOverride;

        return (platform, kind) switch
        {
            // macOS makes no config/data distinction; both conventionally live in one bundle directory.
            (PathPlatform.MacOS, _) => Path.Combine(home, "Library", "Application Support"),
            // Roaming holds small settings that should follow the user between machines; the query log
            // and secrets go to Local so they are never synced off this machine.
            (PathPlatform.Windows, PathKind.Config) => roamingAppData,
            (PathPlatform.Windows, PathKind.Data) => localAppData,
            (_, PathKind.Config) => Path.Combine(home, ".config"),
            _ => Path.Combine(home, ".local", "share"),
        };
    }

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
