using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Input;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.Input;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.App.Settings;
using Bearing.Core.Workspace;

namespace Bearing.App.Editing;

/// <summary>
/// Where a run's results go, and the inline zones' behaviour around it (spec:
/// docs/specs/2026-11-06-inline-results.md). The window keeps rendering the pane; this owns the policy — the
/// placement setting, placing a finished run, the unsaved-edits guard, building each zone's view, focus in
/// and out of zones, and the placement toggles — so it lives in one place rather than in the window's
/// composition root (§9.1).
/// <para>
/// <b>Pane reveal.</b> Inline results never open the pane by themselves: they are already on screen. A run
/// whose results can't be placed inline (<see cref="InlineResultsController.Placement.Untraceable"/>) owes the
/// user the pane instead — now if its tab is shown, otherwise the next time it is.
/// </para>
/// </summary>
public sealed class InlineResultsCoordinator
{
    /// <summary>What the window supplies so a zone's results view behaves like the pane's.</summary>
    /// <param name="Wire">Give a results view the pane's callbacks (paging, export, save, discard, the SQL
    /// preview…), with FK navigation pushed into <c>frames</c> and <c>render</c> called to redraw it after.</param>
    /// <param name="GlobalKeys">Offer a key to the window's global commands; true when one ran.</param>
    /// <param name="ConfirmDiscardEdits">Ask before unsaved grid edits are lost (what, action) → discard?</param>
    /// <param name="CommitOpenEdits">Commit any open cell editor, in the pane and in every zone, so what the
    /// guard counts includes a value still being typed (§9.15).</param>
    /// <param name="ZoneKeys">Offer a key to the zone's own commands (<see cref="CommandIds.GridLeaveInline"/>)
    /// ahead of the grid; true when one ran.</param>
    public sealed record Host(
        Action<ResultView, IResultFrames, Action> Wire,
        Func<KeyEventArgs, bool> GlobalKeys,
        Func<string, string, Task<bool>> ConfirmDiscardEdits,
        Action CommitOpenEdits,
        Func<KeyEventArgs, bool> ZoneKeys);

    /// <summary>The zones' own commands: <see cref="CommandIds.GridLeaveInline"/> (Escape) gives the keyboard
    /// back to the SQL text under the focused zone.</summary>
    public void RegisterCommands(CommandRegistry r)
        => r.Register(KeyCommand.Sync(CommandIds.GridLeaveInline, "Leave inline results", KeyScope.Grid, "Grid",
            () =>
            {
                if (Controller.FocusedZone() is { } zone)
                {
                    Controller.Leave(zone);
                }
            },
            canRun: () => Controller.FocusedZone() is not null));

    private readonly Func<SettingsService?> _settings;
    private readonly Func<EditorTabViewModel?> _selectedTab;
    private readonly Action<EditorTabViewModel?> _rebuildResults;
    private readonly Action<bool> _setPaneVisible;
    private readonly Action<string> _status;
    private readonly Action _focusEditor;
    private readonly Func<Host?> _host;

    // Tabs owed the pane by a run that couldn't be placed inline. Weak, so a closed tab isn't kept alive.
    private readonly ConditionalWeakTable<EditorTabViewModel, object> _revealOnShow = new();

    // The placement last seen, so a settings change can tell a placement switch from any other change.
    private ResultsPlacement? _placement;

    /// <param name="editor">The one editor every tab is shown in.</param>
    /// <param name="settings">Where results.placement is read and written.</param>
    /// <param name="selectedTab">The tab the editor shows now.</param>
    /// <param name="rebuildResults">Render a tab's results in the pane (which then asks
    /// <see cref="PaneVisibilityFor"/>).</param>
    /// <param name="setPaneVisible">Show or collapse the results pane.</param>
    /// <param name="status">Put a line in the status bar.</param>
    /// <param name="focusEditor">Give the editor focus.</param>
    /// <param name="host">The window's side of a zone (read when a zone is first drawn).</param>
    public InlineResultsCoordinator(TextEditor editor, Func<SettingsService?> settings,
        Func<EditorTabViewModel?> selectedTab, Action<EditorTabViewModel?> rebuildResults,
        Action<bool> setPaneVisible, Action<string> status, Action focusEditor, Func<Host?> host)
    {
        _settings = settings;
        _selectedTab = selectedTab;
        _rebuildResults = rebuildResults;
        _setPaneVisible = setPaneVisible;
        _status = status;
        _focusEditor = focusEditor;
        _host = host;
        Controller = new InlineResultsController(editor, () => IsInline, CreateZoneView);
    }

    /// <summary>Draws the zones in the editor.</summary>
    public InlineResultsController Controller { get; }

    /// <summary>Whether results are shown inline under their statements (results.placement).</summary>
    public bool IsInline => _settings()?.Current.ResultsPlacement == ResultsPlacement.Inline;

    /// <summary>The zone holding keyboard focus, when zones are shown — Run and Explain act on its statement
    /// (spec §8).</summary>
    public InlineResultZone? FocusedZone => IsInline ? Controller.FocusedZone() : null;

    /// <summary>
    /// Run (or Run All) <paramref name="scope"/> on the selected tab through <paramref name="execute"/>, then
    /// place its results. Placed in either mode — Pane mode just doesn't draw them — so switching to inline
    /// shows the results already on screen under their statements.
    /// <para>
    /// Asks first when the run would replace results holding unsaved edits (spec §7) — the pane's, or a zone's
    /// inside what it runs. Asked by execution itself, once its refusals and the write confirmation have
    /// passed (<paramref name="execute"/>'s second argument), so a refused run never asks. The edits given up
    /// are reverted only when the run's results actually replaced them: a run that fails before producing any
    /// leaves them as they were. A zone that had focus gets it back on its replacement.
    /// </para>
    /// </summary>
    public async Task RunAsync(RunScope scope, Func<string, Func<Task<bool>>, Task> execute)
    {
        var ran = _selectedTab();
        var refocus = FocusedZone;
        var start = ran is null ? null : Controller.BeginRun(ran);
        PendingEditsGuard.Loss? givenUp = null;

        async Task<bool> ConfirmEditsAsync()
        {
            if (ran is null)
            {
                return true;
            }
            _host()?.CommitOpenEdits();   // a value still in a cell editor is an edit too (§9.15)
            var loss = PendingEditsGuard.ForRun(ran, scope, IsInline);
            if (!await ConfirmLossAsync(loss, "run"))
            {
                _status("Run cancelled — your unsaved edits are kept.");
                return false;
            }
            givenUp = loss;
            return true;
        }

        await execute(scope.Sql, ConfirmEditsAsync);
        if (ran is null || start is null)
        {
            return;
        }
        var placement = Controller.PlaceRun(scope, start);
        if (givenUp is not null && placement != InlineResultsController.Placement.Nothing)
        {
            // Whatever a zone still shows (shown zones only) was not replaced after all — an unplaceable run
            // leaves every zone where it was.
            givenUp.Discard(IsInline ? ran.InlineZones.SelectMany(z => z.AllSets) : null);
        }
        switch (placement)
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
            if (refocus is not null && placement == InlineResultsController.Placement.Inline
                && ran.InlineZones.OrderBy(z => Math.Abs(z.Offset - refocus.Offset)).FirstOrDefault() is { } replaced)
            {
                Controller.Focus(replaced);
            }
        }
    }

    private async Task<bool> ConfirmLossAsync(PendingEditsGuard.Loss loss, string action)
        => loss.IsEmpty || (_host() is { } host
            && await PendingEditsGuard.ConfirmAsync(loss, action, host.ConfirmDiscardEdits));

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

    /// <summary>The pane header's inline toggle: switch to inline (the pane collapses with it, see
    /// <see cref="OnSettingsChanged"/>) — the results it showed are already placed under their statements.</summary>
    public void ShowResultsInline()
    {
        SetPlacement(ResultsPlacement.Inline);
        _setPaneVisible(false);   // also when the setting already said Inline: the button asks for the pane gone
        _focusEditor();
    }

    /// <summary>view.toggleInlineResults: flip results.placement. Persisted through settings, so the settings
    /// window agrees; showing/hiding the zones follows from <see cref="OnSettingsChanged"/>.</summary>
    public void Toggle() => SetPlacement(IsInline ? ResultsPlacement.Pane : ResultsPlacement.Inline);

    /// <summary>Remember the placement in force, so the next settings change can tell whether it switched.
    /// Call when the settings service is attached.</summary>
    public void SyncPlacement() => _placement = _settings()?.Current.ResultsPlacement;

    /// <summary>
    /// Settings changed: re-read whether zones are drawn, and — when the placement itself switched, from
    /// whichever route (the View menu, the palette, the Settings window, a header toggle) — keep the pane in
    /// step, so only one placement is ever on screen (spec §9). To Inline: the pane collapses. To Pane: the
    /// pane renders the tab's results and opens if there are any.
    /// </summary>
    public void OnSettingsChanged(AppSettings settings)
    {
        Controller.Refresh();
        var was = _placement;
        _placement = settings.ResultsPlacement;
        if (was is null || was == settings.ResultsPlacement)
        {
            return;
        }
        if (settings.ResultsPlacement == ResultsPlacement.Inline)
        {
            _setPaneVisible(false);
        }
        else
        {
            _rebuildResults(_selectedTab());
        }
    }

    /// <summary>
    /// The zone focus.results and F6 go to (spec §8): the zone of the statement under the caret — the one
    /// hanging inside <paramref name="statementStart"/>..<paramref name="statementEnd"/> — else the nearest
    /// zone above the caret. Null when the selected tab has none.
    /// </summary>
    public InlineResultZone? ZoneAt(int caret, int statementStart, int statementEnd)
        => _selectedTab() is { } tab
            ? tab.InlineZones.LastOrDefault(z => z.Offset >= statementStart && z.Offset <= statementEnd)
              ?? tab.InlineZones.LastOrDefault(z => z.Offset <= caret)
            : null;

    /// <summary>focus.results in inline mode: focus <see cref="ZoneAt"/>'s grid. False when there is none.</summary>
    public bool FocusZoneAt(int caret, int statementStart, int statementEnd)
        => ZoneAt(caret, statementStart, statementEnd) is { } zone && Controller.Focus(zone);

    /// <summary>The realized view of <see cref="ZoneAt"/>, or null (none, or not drawn yet).</summary>
    public InlineZoneView? ZoneViewAt(int caret, int statementStart, int statementEnd)
        => ZoneAt(caret, statementStart, statementEnd) is { } zone
            ? Controller.Views.FirstOrDefault(v => v.Zone == zone)
            : null;

    /// <summary>Apply a view-wide setting (Stacked / Tabs, inspector size) to every zone's results view, on
    /// every tab — a zone's view outlives a tab switch, so the bound tab's alone would leave the others stale.</summary>
    public void ForEachZoneView(Action<ResultView> apply)
    {
        foreach (var view in Controller.AllViews)
        {
            apply(view.Results);
        }
    }

    /// <summary>One zone's view, built the first time it is drawn: the real results view, wired like the pane's
    /// but with FK navigation and Back in the zone (§4), ✕ and the placement toggle in its header.</summary>
    private InlineZoneView CreateZoneView(EditorTabViewModel tab, InlineResultZone zone)
    {
        var results = new ResultView { IsInline = true };
        void Render()
        {
            results.Caption = zone.Caption;
            results.Notice = zone.StatementRemoved
                ? "The statement these results came from was removed — save or discard the edits"
                : null;
            results.CanGoBack = zone.CanGoBack;
            results.Results = zone.Sets;
        }
        _host()?.Wire(results, zone, Render);
        results.GoBack = () =>
        {
            // Not mid-run: the run's results replace the tab's when it finishes (as the pane's Back refuses).
            if (!tab.IsRunning)
            {
                zone.GoBack();
                Render();
            }
        };
        results.CloseRequested = () => _ = CloseZoneAsync(tab, zone);
        results.ShowInPaneRequested = () => _ = ShowZoneInPaneModeAsync(tab, zone);
        zone.Changed += Render;
        // The zone left its tab: let go of its sets (they may live on in the pane, and their Rows would keep
        // this view's grids subscribed) and of the zone.
        void OnRetired()
        {
            zone.Changed -= Render;
            zone.Retired -= OnRetired;
            results.Results = null;
        }
        zone.Retired += OnRetired;
        Render();
        return new InlineZoneView(zone, results,
            e => _host()?.GlobalKeys(e) == true,
            e => _host()?.ZoneKeys(e) == true);
    }

    /// <summary>✕ on a zone — asks first when it holds unsaved edits (spec §7).</summary>
    private async Task CloseZoneAsync(EditorTabViewModel tab, InlineResultZone zone)
    {
        var refocus = Controller.FocusedZone() == zone;
        _host()?.CommitOpenEdits();
        var loss = PendingEditsGuard.ForZone(tab, zone);
        if (!await ConfirmLossAsync(loss, "close these results"))
        {
            return;
        }
        tab.RemoveInlineZone(zone.Id);
        loss.Discard();   // only the zone held them
        if (refocus)
        {
            Controller.Leave(zone);
        }
    }

    /// <summary>A zone's placement toggle (spec §9): back to the pane, which takes over that zone's sets,
    /// pending edits and Back stack. Refused while the tab runs, and then nothing changes — flipping the
    /// setting anyway would hide every zone with the pane still closed. Asks first when the pane holds edits
    /// that no zone also holds: the takeover replaces its frames (spec §7).</summary>
    private async Task ShowZoneInPaneModeAsync(EditorTabViewModel tab, InlineResultZone zone)
    {
        const string busy = "Wait for the running query to finish before moving these results to the pane.";
        if (tab.IsRunning)
        {
            _status(busy);
            return;
        }
        _host()?.CommitOpenEdits();
        var loss = PendingEditsGuard.ForPaneTakeover(tab);
        if (!await ConfirmLossAsync(loss, "show these results in the pane"))
        {
            return;
        }
        if (!tab.ShowZoneInPane(zone))
        {
            _status(busy);   // it started running while the question was up
            return;
        }
        loss.Discard();
        // The pane renders and opens from the placement switch (OnSettingsChanged); render here too when the
        // setting already said Pane, or there is no settings service to raise the change.
        var switched = IsInline;
        SetPlacement(ResultsPlacement.Pane);
        if ((!switched || _settings() is null) && ReferenceEquals(tab, _selectedTab()))
        {
            _rebuildResults(tab);
            _setPaneVisible(true);
        }
    }

    private void SetPlacement(ResultsPlacement placement)
        => _settings()?.Update(s => s with { ResultsPlacement = placement });
}
