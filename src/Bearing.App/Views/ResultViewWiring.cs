using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Bearing.App.Controls;
using Bearing.App.Editing;
using Bearing.App.Input;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Workspace;

namespace Bearing.App.Views;

/// <summary>
/// The callbacks that make a <see cref="ResultView"/> work — paging, count, export, FK navigation, Back, save,
/// discard, the pending-SQL window, status, the shared grid commands — applied the same way to the results
/// pane's view and to every inline zone's (spec docs/specs/2026-11-06-inline-results.md §5), so a zone does what
/// the pane does by construction rather than by a second copy of the wiring in the window.
/// <para>
/// What differs is only <i>where</i> FK navigation and Back land: the results context passed in — the selected
/// tab for the pane, the zone for a zone — and what to redraw afterwards.
/// </para>
/// </summary>
/// <param name="zonesShown">Whether results are shown inline (the coordinator's answer — one source).</param>
/// <param name="paneVisible">Whether the results pane is open now.</param>
internal sealed class ResultViewWiring(
    Func<ShellViewModel?> vm,
    Func<KeyDispatcher?> dispatcher,
    Func<GridCommandRouter?> router,
    Func<ResultSetViewModel, Task> showPendingSql,
    Func<bool> zonesShown,
    Func<bool> paneVisible)
{

    /// <summary>The status line when the discard question was answered but the results moved meanwhile.</summary>
    public const string BackAbandoned = "Not gone back — the results changed while the question was open.";

    // Every view wired so far, with its redraw: a run's sets are shared by the pane and their zone, so a save
    // or discard in one must reach the other. Weak — a zone's view goes with its zone.
    private readonly ConditionalWeakTable<ResultView, Action> _views = new();

    /// <summary>The wired views other than <paramref name="except"/> that show <paramref name="rs"/>.</summary>
    private (ResultView View, Action Render)[] Others(ResultView except, ResultSetViewModel rs)
        => _views.Where(kv => !ReferenceEquals(kv.Key, except) && kv.Key.Shows(rs))
            .Select(kv => (kv.Key, kv.Value)).ToArray();

    /// <summary>
    /// Back on <paramref name="view"/>: pop its frames' current frame. Refused while an FK lookup is in flight on
    /// the tab (it would land on the frame Back popped); allowed during an ordinary query run. Refused here, not in
    /// grid.back's canRun, so the key and the button both say why. A frame holding unsaved edits that nothing else
    /// holds asks first (spec §7) — an open cell editor counts (§9.15) — and Discard reverts them.
    /// </summary>
    private async Task GoBackAsync(ResultView view, Func<IResultFrames?> frames, Action render)
    {
        if (vm() is not { Workspace.SelectedTab: { } tab } shell)
        {
            return;
        }
        try
        {
            if (tab.BusyReason("going back") is { } busy)
            {
                shell.StatusText = busy;
                return;
            }
            if (frames() is not { } target || !target.CanGoBack)
            {
                return;   // nothing to go back to (a stale key or click): nothing to ask about either
            }
            view.CommitOpenEdits();
            var loss = PendingEditsGuard.ForBack(tab, target, zonesShown());
            var popped = target.Current;
            if (!loss.IsEmpty)
            {
                var frame = target.Current;
                if (shell.Dialogs is not { } dialogs || !await dialogs.ConfirmDiscardEditsAsync(loss.Text, "go back"))
                {
                    return;
                }
                // The answer took time: a lookup or save may have started, the frame changed or was popped, or a
                // run replaced the zone under the question.
                if (tab.BusyReason("going back") is not null || !target.CanGoBack || !ReferenceEquals(frame, target.Current)
                    || (target is InlineResultZone zone && !tab.InlineZones.Contains(zone)))
                {
                    shell.StatusText = BackAbandoned;
                    return;
                }
            }
            target.GoBack();
            loss.Discard();   // a no-op when nothing was pending
            render();
            // A zone's frame whose edits only the hidden pane also holds: kept there, and said so, like ✕.
            if (target is InlineResultZone && !paneVisible()
                && PendingEditsGuard.KeptMessage(PendingEditsGuard.KeptInPane(tab, popped),
                    dispatcher()?.Keymap.DisplayGesture(CommandIds.ViewToggleResults)) is { } kept)
            {
                shell.StatusText = kept;
            }
        }
        catch (Exception ex)
        {
            // Fire-and-forget from a click or key: an error ends here, on the status line, not unobserved.
            shell.StatusText = $"Back failed: {SafeErrorText.Of(ex)}";
        }
    }

    /// <param name="view">The view to wire.</param>
    /// <param name="frames">Where FK navigation pushes and Back pops, read at the time of the action.</param>
    /// <param name="render">Redraw the view from its context (after navigation, Back, discard).</param>
    public void Apply(ResultView view, Func<IResultFrames?> frames, Action render)
    {
        _views.AddOrUpdate(view, render);
        view.CommandDispatcher = dispatcher();
        view.Router ??= router();
        if (vm() is { } shell)
        {
            view.ViewMode = shell.ResultsViewMode;
            view.InspectorFontSize = shell.InspectorFontSize;
        }
        view.ViewModeChanged = mode => { if (vm() is { } s) s.ResultsViewMode = mode; };
        view.InspectorFontSizeChanged = size =>
            vm()?.SettingsService.Update(s => s with { InspectorFontSize = (int)size });

        view.LoadMore = rs => vm()?.Execution.LoadMoreAsync(rs) ?? Task.CompletedTask;
        view.CountTotal = rs => vm()?.Execution.CountTotalAsync(rs) ?? Task.CompletedTask;
        view.FetchAll = async rs => { if (vm() is { } s) await s.Execution.FetchAllAsync(rs); };
        view.Export = (rs, format) => vm()?.Execution.ExportAsync(rs, format) ?? Task.CompletedTask;
        view.NavigateForeignKey = async (rs, col, row) =>
        {
            if (vm() is not { } s || frames() is not { } target)
            {
                return;
            }
            // The tab's own frames are the pane's: navigating there is the default route (null).
            await s.Execution.NavigateForeignKeyAsync(rs, col, row, target is EditorTabViewModel ? null : target);
            render();
        };
        view.GoBack = () => _ = GoBackAsync(view, frames, render);
        view.Status = text => { if (vm() is { } s) s.StatusText = text; };
        view.SaveChanges = async rs =>
        {
            if (vm() is not { } s)
            {
                return;
            }
            await s.Execution.SaveChangesAsync(rs);   // applies in one tx, updating affected rows in place
            view.RefreshRowHighlights();              // clear the pending tints (no full rebuild → scroll kept)
            foreach (var (other, _) in Others(view, rs))
            {
                other.RefreshRowHighlights();         // the same sets in the pane and their zone
            }
        };
        view.DiscardChanges = async rs =>
        {
            if (vm() is not { } s)
            {
                return;
            }
            var others = Others(view, rs);
            await s.Execution.DiscardChangesAsync(rs); // reverts pending changes in place
            render();                                  // re-render the restored rows
            foreach (var (_, redraw) in others)
            {
                redraw();                              // the same sets in the pane and their zone
            }
        };
        view.ShowPendingSql = showPendingSql;
    }
}
