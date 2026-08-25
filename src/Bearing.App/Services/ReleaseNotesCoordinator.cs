using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Updates;

namespace Bearing.App.Services;

/// <summary>
/// Owns when release notes are fetched and when they are put on screen: the Help ▸ What's New path, the
/// update strip's "what's new" link, and the once-per-upgrade "What's New in 0.4.0" that greets a user after
/// an update installs.
/// <para>
/// Deliberately free of Avalonia so the whole policy is unit-testable (§2.5/§4.3) — the composition root
/// supplies <see cref="Show"/> and <see cref="Report"/> as sinks that marshal to the UI thread, exactly as
/// <see cref="UpdateCoordinator"/> is given its shutdown and status-bar callbacks.
/// </para>
/// </summary>
public sealed class ReleaseNotesCoordinator
{
    private readonly IReleaseNotes _notes;
    private readonly string _runningVersion;
    private readonly Func<string?> _lastSeenVersion;
    private readonly Action<string> _recordSeen;
    private readonly bool _isFreshInstall;

    /// <summary>
    /// One fetch at a time. The startup check runs on a background thread while the menu item comes off the
    /// UI thread, so without this an upgrade launch could ask GitHub twice for the same list — and the API
    /// budget it spends is 60/hr for the whole network (see <c>UpdateFeed.AccessToken</c>).
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The release list, once fetched. Held for the session so reopening the dialog is free: releases don't
    /// change while the app runs, and the alternative is spending a rate-limited request on every menu click.
    /// </summary>
    private IReadOnlyList<ReleaseNote>? _cached;

    /// <param name="notes">The release feed.</param>
    /// <param name="runningVersion">This build's version, as <c>AppVersion.Display</c> reports it.</param>
    /// <param name="lastSeenVersion">The version whose notes were last shown, read live from settings.</param>
    /// <param name="recordSeen">Persist a version as seen. Called only after notes were actually fetched.</param>
    /// <param name="isFreshInstall">Whether this user has never run Bearing before. Needed because a null
    /// <c>LastSeenVersion</c> cannot tell a first launch from an upgrade out of a build that predates the
    /// setting — and treating the second as the first swallows the notes for the very release that ships
    /// this feature, for every existing user.</param>
    public ReleaseNotesCoordinator(
        IReleaseNotes notes,
        string runningVersion,
        Func<string?> lastSeenVersion,
        Action<string> recordSeen,
        bool isFreshInstall = false)
    {
        _notes = notes;
        _runningVersion = runningVersion;
        _lastSeenVersion = lastSeenVersion;
        _recordSeen = recordSeen;
        _isFreshInstall = isFreshInstall;
    }

    /// <summary>
    /// Puts the notes on screen: the full history, scrolled to <c>focusVersion</c> when one is given. Set by
    /// the composition root; null (headless, tests) means nothing is shown and the fetch is all that happens.
    /// </summary>
    public Action<IReadOnlyList<ReleaseNote>, string?>? Show { get; set; }

    /// <summary>
    /// Status-bar sink, as <see cref="UpdateCoordinator.Report"/> is. Only used for the paths the user is
    /// waiting on — never by <see cref="ShowWhatsNewIfUpdatedAsync"/>, which must stay silent (see there).
    /// </summary>
    public Action<string>? Report { get; set; }

    /// <summary>
    /// Open the notes because the user asked — the Help menu, or the update strip's link. Reports its
    /// outcome either way: a menu item that appears to do nothing is worse than a slow one.
    /// </summary>
    /// <param name="focusVersion">The version to scroll to, or null to open at the newest.</param>
    public async Task OpenAsync(string? focusVersion = null, CancellationToken ct = default)
    {
        IReadOnlyList<ReleaseNote> notes;
        try
        {
            notes = await LoadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Report?.Invoke($"Couldn't load release notes: {ex.Message}");
            return;
        }

        if (notes.Count == 0)
        {
            // A reachable feed with nothing in it is not a failure, but opening an empty window would look
            // like one. Say so in the status bar instead.
            Report?.Invoke("No release notes have been published yet.");
            return;
        }

        Show?.Invoke(notes, focusVersion);
    }

    /// <summary>
    /// The startup path: show this version's notes once, the first time the app runs after an update.
    /// <para>
    /// Silent throughout, including on failure — it runs unbidden during launch, and a user who opened the
    /// app to run a query has not asked to be told that GitHub was unreachable. A failed fetch also leaves
    /// the last-seen version alone, so the notes for an upgrade are shown on the next launch that does have
    /// a network rather than being lost.
    /// </para>
    /// </summary>
    public async Task ShowWhatsNewIfUpdatedAsync(CancellationToken ct = default)
    {
        var lastSeen = _lastSeenVersion();
        if (string.Equals(lastSeen, _runningVersion, StringComparison.Ordinal)) return;

        if (string.IsNullOrEmpty(lastSeen) && _isFreshInstall)
        {
            // First run of a fresh install. There is no "what's new" for someone who has never seen the old
            // one — greeting them with a changelog would be noise. Record where they came in and say nothing.
            _recordSeen(_runningVersion);
            return;
        }

        // A null last-seen on a machine that has run Bearing before is the upgrade *into* this feature: the
        // setting did not exist in the build they came from. Those notes are the ones most worth showing, so
        // fall through rather than silently recording.

        IReadOnlyList<ReleaseNote> notes;
        try
        {
            notes = await LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        // Recorded on a successful fetch even when this version has no entry — a build that isn't a
        // published release (a dev run, a version whose notes were never written) would otherwise re-ask
        // GitHub on every single launch, forever, for an answer that will not change.
        _recordSeen(_runningVersion);

        foreach (var note in notes)
        {
            if (!string.Equals(note.Version, _runningVersion, StringComparison.OrdinalIgnoreCase)) continue;
            // The whole history, focused on this version: a user who skipped 0.3 on the way from 0.2 to 0.4
            // should be able to scroll to what they missed rather than only seeing the last hop.
            Show?.Invoke(notes, note.Version);
            return;
        }
    }

    /// <summary>The release list, fetched once per session and reused. Throws what the feed throws.</summary>
    private async Task<IReadOnlyList<ReleaseNote>> LoadAsync(CancellationToken ct)
    {
        if (_cached is { } ready) return ready;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked under the gate: whoever held it may have been fetching this very list.
            return _cached ??= await _notes.FetchAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
