using Bearing.Core.Updates;
using Xunit;

namespace Bearing.Updates.Tests;

/// <summary>
/// The pure parts of <see cref="GitHubReleaseNotes"/>: turning the feed URL into an API path, tags into
/// versions, and versions into an order. The HTTP call itself is not covered — it needs GitHub — but
/// everything that decides <i>what</i> is asked for and <i>how</i> the answer is arranged is.
/// </summary>
public class GitHubReleaseNotesTests
{
    [Theory]
    [InlineData("https://github.com/moberghr/bearing", "moberghr/bearing")]
    [InlineData("https://github.com/moberghr/bearing/", "moberghr/bearing")]
    [InlineData("https://github.com/moberghr/bearing.git", "moberghr/bearing")]
    public void Slug_reduces_a_repo_url_to_owner_and_name(string url, string expected)
        => Assert.Equal(expected, GitHubReleaseNotes.Slug(url));

    [Fact]
    public void Slug_rejects_a_url_that_names_no_repository()
        => Assert.Throws<ArgumentException>(() => GitHubReleaseNotes.Slug("https://github.com"));

    [Fact]
    public void The_feed_url_this_app_ships_with_resolves()
        => Assert.Equal("moberghr/bearing", GitHubReleaseNotes.Slug(UpdateFeed.RepoUrl));

    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("V0.3.0", "0.3.0")]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("v0.4.0-beta.1", "0.4.0-beta.1")]
    public void StripTagPrefix_drops_only_the_v(string tag, string expected)
        => Assert.Equal(expected, GitHubReleaseNotes.StripTagPrefix(tag));

    [Fact]
    public void StripTagPrefix_keeps_a_tag_that_merely_starts_with_a_letter()
    {
        // "version-2" is not "v" + a version, and mangling it into "ersion-2" would silently misname a
        // release rather than fail visibly.
        Assert.Equal("version-2", GitHubReleaseNotes.StripTagPrefix("version-2"));
    }

    [Fact]
    public void Compare_orders_by_semver_not_by_text()
    {
        // The one that matters: ordinal string order puts 0.10.0 *before* 0.9.0, which would file a newer
        // release under an older one in a dialog that reads top-down.
        Assert.True(GitHubReleaseNotes.Compare(Note("0.10.0"), Note("0.9.0")) > 0);
        Assert.True(GitHubReleaseNotes.Compare(Note("0.9.0"), Note("0.10.0")) < 0);
        Assert.Equal(0, GitHubReleaseNotes.Compare(Note("1.2.3"), Note("1.2.3")));
    }

    [Fact]
    public void Compare_ignores_a_prerelease_tail()
    {
        // Pre-releases are filtered out before this is reached; it must still not throw on one that leaks
        // through a hand-cut tag.
        Assert.True(GitHubReleaseNotes.Compare(Note("0.4.0-beta.1"), Note("0.3.0")) > 0);
    }

    [Fact]
    public void Compare_falls_back_to_text_for_a_tag_that_is_not_a_version()
        => Assert.True(GitHubReleaseNotes.Compare(Note("nightly"), Note("beta")) > 0);

    private static ReleaseNote Note(string version) => new(version, version, null, "");
}
