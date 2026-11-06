using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Bearing.App.Controls;
using Bearing.App.Input;
using Bearing.App.ViewModels;

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
internal sealed class ResultViewWiring(
    Func<ShellViewModel?> vm,
    Func<KeyDispatcher?> dispatcher,
    Func<GridCommandRouter?> router,
    Func<ResultSetViewModel, Task> showPendingSql)
{
    // Every view wired so far, with its redraw: a run's sets are shared by the pane and their zone, so a save
    // or discard in one must reach the other. Weak — a zone's view goes with its zone.
    private readonly ConditionalWeakTable<ResultView, Action> _views = new();

    /// <summary>The wired views other than <paramref name="except"/> that show <paramref name="rs"/>.</summary>
    private (ResultView View, Action Render)[] Others(ResultView except, ResultSetViewModel rs)
        => _views.Where(kv => !ReferenceEquals(kv.Key, except) && kv.Key.Shows(rs))
            .Select(kv => (kv.Key, kv.Value)).ToArray();

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
        view.GoBack = () =>
        {
            // Not mid-run: the run's results replace the tab's when it finishes, and a frame swapped in between
            // would be taken for them (InlineResultsController.PlaceRun) — FK navigation refuses for the same reason.
            if (vm()?.Workspace.SelectedTab is { IsRunning: false })
            {
                frames()?.GoBack();
            }
            render();
        };
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
