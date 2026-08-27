namespace Bearing.Core.Updates;

/// <summary>
/// One published release, as the app shows it: the version it shipped, when, and the notes that came with
/// it. <paramref name="Markdown"/> is the release description exactly as written — <c>build/velopack.sh</c>
/// feeds the same text to <c>vpk pack --releaseNotes</c> and to the GitHub release body, so what a user
/// reads here is what the Releases page says.
/// </summary>
/// <param name="Version">Bare semver, no <c>v</c> prefix — comparable with <c>AppVersion.Display</c>.</param>
/// <param name="Title">The release's display name, falling back to the version when it has none.</param>
/// <param name="Published">When it went out. Null when the feed doesn't say.</param>
/// <param name="Markdown">The notes. Empty for a release published without any.</param>
/// <param name="Url">The release page, for the "view on GitHub" escape hatch — the notes carry issue refs
/// and links that only resolve there. Null when the feed doesn't say.</param>
public sealed record ReleaseNote(
    string Version,
    string Title,
    DateTimeOffset? Published,
    string Markdown,
    string? Url = null);

/// <summary>
/// The release history, newest first. Separate from <see cref="IUpdateService"/> on purpose: that answers
/// "is there something newer", which is forward-looking and per-channel, while this answers "what changed",
/// which spans every version including the one already running.
/// <para>
/// Throws on failure like <see cref="IUpdateService"/> does, and for the same reason: what an unreachable
/// feed <i>means</i> is the caller's call. Nothing here is offline-capable — release notes are read from the
/// feed each session, so a launch with no network simply has none to show (which is why the app never
/// records a version as "seen" off the back of a failed fetch).
/// </para>
/// </summary>
public interface IReleaseNotes
{
    /// <summary>
    /// The most recent releases, newest first. Drafts and pre-releases are excluded — the app never
    /// installs one, so describing it to the user would be describing a version they cannot get.
    /// </summary>
    Task<IReadOnlyList<ReleaseNote>> FetchAsync(CancellationToken ct = default);
}
