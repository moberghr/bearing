using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.Core.Updates;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The release-notes policy: when notes are fetched, when they go on screen, and when a version counts as
/// seen. All of it lives in the coordinator precisely so it can be tested here — the dialog it drives
/// cannot be (§4.3).
/// </summary>
public class ReleaseNotesCoordinatorTests
{
    /// <summary>Everything a test needs to watch: the shown notes, the status messages, the stored version.</summary>
    private sealed class Harness
    {
        public FakeReleaseNotes Feed { get; } = new();
        public string? LastSeen { get; set; }

        /// <summary>Whether this models a machine that has never run Bearing.</summary>
        public bool FreshInstall { get; set; }
        public List<string> Reported { get; } = new();
        public List<(IReadOnlyList<ReleaseNote> Notes, string? Focus)> Shown { get; } = new();

        public ReleaseNotesCoordinator Build(string runningVersion) => new(
            Feed,
            runningVersion,
            lastSeenVersion: () => LastSeen,
            recordSeen: version => LastSeen = version,
            isFreshInstall: FreshInstall)
        {
            Show = (notes, focus) => Shown.Add((notes, focus)),
            Report = message => Reported.Add(message),
        };
    }

    [Fact]
    public async Task A_fresh_install_records_its_version_without_showing_anything()
    {
        // Nobody's first launch should open a changelog for software they have never run.
        var h = new Harness { FreshInstall = true };
        h.Feed.Published("0.3.0");

        await h.Build("0.3.0").ShowWhatsNewIfUpdatedAsync();

        Assert.Empty(h.Shown);
        Assert.Equal("0.3.0", h.LastSeen);
        Assert.Equal(0, h.Feed.Fetches);
    }

    [Fact]
    public async Task An_upgrade_out_of_a_build_that_predates_the_setting_still_shows()
    {
        // LastSeenVersion is null for every copy installed before this feature existed. Reading that as a
        // fresh install would swallow the notes for the very release that introduces the dialog, for
        // everyone who already uses Bearing — the one audience it is for.
        var h = new Harness { LastSeen = null, FreshInstall = false };
        h.Feed.Published("0.3.0");

        await h.Build("0.3.0").ShowWhatsNewIfUpdatedAsync();

        Assert.Equal("0.3.0", Assert.Single(h.Shown).Focus);
        Assert.Equal("0.3.0", h.LastSeen);
    }

    [Fact]
    public async Task An_upgrade_shows_this_version_and_records_it()
    {
        var h = new Harness { LastSeen = "0.2.1" };
        h.Feed.Published("0.3.0").Published("0.2.1");

        await h.Build("0.3.0").ShowWhatsNewIfUpdatedAsync();

        var (notes, focus) = Assert.Single(h.Shown);
        Assert.Equal("0.3.0", focus);
        // The whole history, not just the last hop — someone who skipped a version can scroll to it.
        Assert.Equal(2, notes.Count);
        Assert.Equal("0.3.0", h.LastSeen);
    }

    [Fact]
    public async Task An_upgrade_shown_once_is_not_shown_again()
    {
        var h = new Harness { LastSeen = "0.2.1" };
        h.Feed.Published("0.3.0");
        var coordinator = h.Build("0.3.0");

        await coordinator.ShowWhatsNewIfUpdatedAsync();
        await coordinator.ShowWhatsNewIfUpdatedAsync();

        Assert.Single(h.Shown);
    }

    [Fact]
    public async Task A_failed_fetch_stays_silent_and_leaves_the_version_unseen()
    {
        // An offline launch must not consume the upgrade: the notes are owed on the next launch that can
        // reach the feed, and the user did not open the app to be told GitHub was down.
        var h = new Harness { LastSeen = "0.2.1" };
        h.Feed.FetchThrows = new InvalidOperationException("no network");

        await h.Build("0.3.0").ShowWhatsNewIfUpdatedAsync();

        Assert.Empty(h.Shown);
        Assert.Empty(h.Reported);
        Assert.Equal("0.2.1", h.LastSeen);
    }

    [Fact]
    public async Task A_version_with_no_published_notes_shows_nothing_but_still_counts_as_seen()
    {
        // Otherwise a dev build, or a version whose notes were never written, re-asks GitHub every launch
        // for an answer that will not change.
        var h = new Harness { LastSeen = "0.2.1" };
        h.Feed.Published("0.2.1");

        await h.Build("0.3.0").ShowWhatsNewIfUpdatedAsync();

        Assert.Empty(h.Shown);
        Assert.Equal("0.3.0", h.LastSeen);
    }

    [Fact]
    public async Task Opening_from_the_menu_shows_the_history_unfocused()
    {
        var h = new Harness { LastSeen = "0.3.0" };
        h.Feed.Published("0.3.0").Published("0.2.1");

        await h.Build("0.3.0").OpenAsync();

        var (notes, focus) = Assert.Single(h.Shown);
        Assert.Null(focus);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public async Task Opening_from_the_update_strip_focuses_the_offered_version()
    {
        var h = new Harness { LastSeen = "0.3.0" };
        h.Feed.Published("0.4.0").Published("0.3.0");

        await h.Build("0.3.0").OpenAsync("0.4.0");

        Assert.Equal("0.4.0", Assert.Single(h.Shown).Focus);
    }

    [Fact]
    public async Task An_explicit_open_reports_a_failure_rather_than_swallowing_it()
    {
        // The opposite of the startup path: the user is waiting for a window, so silence would read as a
        // broken menu item.
        var h = new Harness();
        h.Feed.FetchThrows = new InvalidOperationException("GitHub returned 403.");

        await h.Build("0.3.0").OpenAsync();

        Assert.Empty(h.Shown);
        Assert.Contains("GitHub returned 403.", Assert.Single(h.Reported));
    }

    [Fact]
    public async Task An_empty_feed_reports_rather_than_opening_a_blank_window()
    {
        var h = new Harness();

        await h.Build("0.3.0").OpenAsync();

        Assert.Empty(h.Shown);
        Assert.Contains("No release notes", Assert.Single(h.Reported));
    }

    [Fact]
    public async Task An_explicit_open_never_records_a_version_as_seen()
    {
        // Reading the notes on purpose is not the same as being greeted by them; letting a menu click
        // consume the upgrade greeting would hide it from the launch it belongs to.
        var h = new Harness { LastSeen = "0.2.1" };
        h.Feed.Published("0.3.0");

        await h.Build("0.3.0").OpenAsync();

        Assert.Equal("0.2.1", h.LastSeen);
    }

    [Fact]
    public async Task The_feed_is_asked_once_per_session_however_many_times_it_is_opened()
    {
        // The API budget is 60/hr for the whole network (UpdateFeed.AccessToken); releases do not change
        // while the app runs, so re-opening the window must be free.
        var h = new Harness { LastSeen = "0.2.1" };
        h.Feed.Published("0.3.0");
        var coordinator = h.Build("0.3.0");

        await coordinator.ShowWhatsNewIfUpdatedAsync();
        await coordinator.OpenAsync();
        await coordinator.OpenAsync("0.3.0");

        Assert.Equal(1, h.Feed.Fetches);
        Assert.Equal(3, h.Shown.Count);
    }

    [Fact]
    public async Task Concurrent_opens_still_only_fetch_once()
    {
        var h = new Harness { LastSeen = "0.2.1" };
        h.Feed.Published("0.3.0");
        var coordinator = h.Build("0.3.0");

        await Task.WhenAll(coordinator.OpenAsync(), coordinator.OpenAsync(), coordinator.OpenAsync());

        Assert.Equal(1, h.Feed.Fetches);
    }

    [Fact]
    public async Task A_version_that_differs_only_in_case_still_matches()
    {
        var h = new Harness { LastSeen = "0.2.1" };
        h.Feed.Notes.Add(new ReleaseNote("0.3.0-BETA.1", "Bearing 0.3.0-BETA.1", null, "notes"));

        await h.Build("0.3.0-beta.1").ShowWhatsNewIfUpdatedAsync();

        Assert.Single(h.Shown);
    }
}
