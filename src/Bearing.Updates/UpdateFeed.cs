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
    /// Optional read credential for the feed, from <c>BEARING_UPDATE_TOKEN</c>. Null — the normal case — means
    /// anonymous access, which is all a public repository needs.
    /// <para>
    /// It exists for the cases where anonymous isn't enough: GitHub's unauthenticated API rate limit (60/hr
    /// per IP, which a shared egress address can exhaust), or a private fork of this repo. Wherever a token is
    /// wanted, it comes from the environment and is <b>never</b> persisted by us — a token compiled into the
    /// binary is a published secret, and writing one to disk is exactly the on-disk secret posture §1.1
    /// removed. A token that doesn't work is reported once, quietly, and not retried (see the app's update
    /// coordinator).
    /// </para>
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
