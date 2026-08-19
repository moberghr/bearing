namespace Bearing.Updates;

/// <summary>
/// Where releases come from. GitHub Releases on the app's own repository — the same place the source and
/// the issues live, so a release is one <c>vpk upload github</c> away (see docs/RELEASING.md).
/// </summary>
public static class UpdateFeed
{
    /// <summary>The repository whose Releases carry the feed.</summary>
    public const string RepoUrl = "https://github.com/moberghr/bearing";

    /// <summary>
    /// Read access for the feed while the repository is <b>private</b>, from <c>BEARING_UPDATE_TOKEN</c>.
    /// <para>
    /// A private feed needs a credential, and the one thing we will not do is ship one: a token compiled
    /// into the binary is a published secret, and writing one to disk is exactly the on-disk secret posture
    /// §1.1 removed. So it comes from the environment, we never persist it, and no token simply means the
    /// check can't reach the feed — reported once, quietly (see the app's update coordinator).
    /// </para>
    /// <para>When the repository goes public this stops mattering: the same code path works with no token.</para>
    /// </summary>
    public static string? AccessToken
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("BEARING_UPDATE_TOKEN");
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
    }
}
