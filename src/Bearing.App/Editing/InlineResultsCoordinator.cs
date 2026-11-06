using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AvaloniaEdit;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.App.Settings;
using Bearing.Core.Workspace;

namespace Bearing.App.Editing;

/// <summary>
/// Where a run's results go, and when the results pane opens for them (spec:
/// docs/specs/2026-11-06-inline-results.md). The window keeps rendering the pane; this owns the policy — the
/// placement setting, placing a finished run, the two placement toggles and ⤢ — so it lives in one place
/// rather than in the window's composition root (§9.1).
/// <para>
/// <b>Pane reveal.</b> Inline results never open the pane by themselves: they are already on screen. A run
/// whose results can't be placed inline (<see cref="InlineResultsController.Placement.Untraceable"/>) owes the
/// user the pane instead — now if its tab is shown, otherwise the next time it is.
/// </para>
/// </summary>
public sealed class InlineResultsCoordinator
{
    private readonly Func<SettingsService?> _settings;
    private readonly Func<EditorTabViewModel?> _selectedTab;
    private readonly Action<EditorTabViewModel?> _rebuildResults;
    private readonly Action<bool> _setPaneVisible;
    private readonly Action<string> _status;
    private readonly Action _focusEditor;

    // Tabs owed the pane by a run that couldn't be placed inline. Weak, so a closed tab isn't kept alive.
    private readonly ConditionalWeakTable<EditorTabViewModel, object> _revealOnShow = new();

    /// <param name="editor">The one editor every tab is shown in.</param>
    /// <param name="settings">Where results.placement is read and written.</param>
    /// <param name="selectedTab">The tab the editor shows now.</param>
    /// <param name="rebuildResults">Render a tab's results in the pane (which then asks
    /// <see cref="PaneVisibilityFor"/>).</param>
    /// <param name="setPaneVisible">Show or collapse the results pane.</param>
    /// <param name="status">Put a line in the status bar.</param>
    /// <param name="focusEditor">Give the editor focus.</param>
    public InlineResultsCoordinator(TextEditor editor, Func<SettingsService?> settings,
        Func<EditorTabViewModel?> selectedTab, Action<EditorTabViewModel?> rebuildResults,
        Action<bool> setPaneVisible, Action<string> status, Action focusEditor)
    {
        _settings = settings;
        _selectedTab = selectedTab;
        _rebuildResults = rebuildResults;
        _setPaneVisible = setPaneVisible;
        _status = status;
        _focusEditor = focusEditor;
        Controller = new InlineResultsController(editor, () => IsInline, (t, z) => OpenInPane(t, z), ShowZoneInPaneMode);
    }

    /// <summary>Draws the zones in the editor.</summary>
    public InlineResultsController Controller { get; }

    /// <summary>Whether results are shown inline under their statements (results.placement).</summary>
    public bool IsInline => _settings()?.Current.ResultsPlacement == ResultsPlacement.Inline;

    /// <summary>
    /// Run (or Run All) <paramref name="scope"/> on the selected tab through <paramref name="execute"/>, then
    /// place its results. Placed in either mode — Pane mode just doesn't draw them — so switching to inline
    /// shows the results already on screen under their statements.
    /// </summary>
    public async Task RunAsync(RunScope scope, Func<string, Task> execute)
    {
        var ran = _selectedTab();
        var start = ran is null ? null : Controller.BeginRun(ran);
        await execute(scope.Sql);
        if (ran is null || start is null)
        {
            return;
        }
        switch (Controller.PlaceRun(scope, start))
        {
            case InlineResultsController.Placement.Untraceable:
                _revealOnShow.AddOrUpdate(ran, true);
                break;
            case InlineResultsController.Placement.Inline:
                _revealOnShow.Remove(ran);   // this run's results are on screen; an older debt is settled
                break;
        }
        // Execution is per tab: the user may have switched away while it ran. Its results then render (and
        // the pane opens, if owed) when they switch back.
        if (ReferenceEquals(ran, _selectedTab()))
        {
            _rebuildResults(ran);
        }
    }

    /// <summary>
    /// Whether the pane should be shown after <paramref name="tab"/>'s results were rendered in it, or null to
    /// leave it as it is: shown with results and collapsed without them — except that in inline mode results
    /// don't open it, unless a run that couldn't be placed inline is owed it.
    /// </summary>
    public bool? PaneVisibilityFor(EditorTabViewModel? tab)
    {
        if (tab?.Results is not { Count: > 0 })
        {
            return false;
        }
        if (_revealOnShow.Remove(tab) || !IsInline)
        {
            return true;
        }
        return null;
    }

    /// <summary>The pane header's inline toggle: switch to inline and get the pane out of the way — the
    /// results it showed are already placed under their statements.</summary>
    public void ShowResultsInline()
    {
        SetPlacement(ResultsPlacement.Inline);
        _setPaneVisible(false);
        _focusEditor();
    }

    /// <summary>view.toggleInlineResults: flip results.placement. Persisted through settings, so the settings
    /// window agrees; showing/hiding the zones follows from <see cref="OnSettingsChanged"/>.</summary>
    public void Toggle() => SetPlacement(IsInline ? ResultsPlacement.Pane : ResultsPlacement.Inline);

    /// <summary>Settings changed: re-read whether zones are drawn.</summary>
    public void OnSettingsChanged(AppSettings settings) => Controller.Refresh();

    /// <summary>A zone's placement toggle: back to the pane, showing that zone's sets there. The pane is
    /// opened first: refused while the tab runs, and then nothing changes — flipping the setting anyway would
    /// hide every zone with the pane still closed.</summary>
    private void ShowZoneInPaneMode(EditorTabViewModel tab, InlineResultZone zone)
    {
        if (OpenInPane(tab, zone))
        {
            SetPlacement(ResultsPlacement.Pane);
        }
    }

    /// <summary>⤢ on an inline zone: its sets become the tab's results, shown in the pane.
    /// Refused while the tab runs (<see cref="EditorTabViewModel.ShowInlineZoneInResults"/>).</summary>
    private bool OpenInPane(EditorTabViewModel tab, InlineResultZone zone)
    {
        if (!tab.ShowInlineZoneInResults(zone))
        {
            _status("Wait for the running query to finish before opening these results in the pane.");
            return false;
        }
        if (ReferenceEquals(tab, _selectedTab()))
        {
            _rebuildResults(tab);
            _setPaneVisible(true);
        }
        return true;
    }

    private void SetPlacement(ResultsPlacement placement)
        => _settings()?.Update(s => s with { ResultsPlacement = placement });
}
