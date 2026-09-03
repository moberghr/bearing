using System.Reflection;

namespace Bearing.App;

/// <summary>
/// The running build's version, read once from the assembly's informational version.
/// <para>
/// For a release that string comes from the git tag: publishing a GitHub Release triggers
/// <c>.github/workflows/release.yml</c>, which passes the tag to <c>dotnet publish -p:Version=</c>, so it is
/// the same string the release feed compares against by construction rather than by discipline. A build from
/// source instead reports <c>&lt;Version&gt;</c> from Directory.Build.props, which is the placeholder
/// <c>0.0.0-dev</c> — a local build is not a release and should not claim to be one. See docs/RELEASING.md.
/// </para>
/// <para>
/// One source of truth for every surface that shows it: the About dialog, the status bar, and anything that
/// comes later. Static data, so chrome binds it with <c>{x:Static}</c> rather than routing it through a
/// view-model that would only be passing a constant along.
/// </para>
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Exactly what the assembly reports, including the <c>+&lt;git-sha&gt;</c> the SDK appends. Worth showing
    /// somewhere a user can copy from: it identifies the exact commit a build came from, which "0.2.1" alone
    /// does not.
    /// </summary>
    public static string Full { get; } = Read();

    /// <summary>The version without build metadata — <c>0.2.1</c>. What a release is called.</summary>
    public static string Display { get; } = Strip(Full);

    /// <summary>The same, prefixed for use as a bare label in chrome: <c>v0.2.1</c>.</summary>
    public static string Label { get; } = $"v{Display}";

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) info = assembly.GetName().Version?.ToString();
        return string.IsNullOrEmpty(info) ? "unknown" : info;
    }

    /// <summary>Drop the <c>+metadata</c> suffix. Everything before the first <c>+</c> is the semver.</summary>
    internal static string Strip(string version)
    {
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
