using System;
using Avalonia.Controls;
using Bearing.App.Controls;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

/// <summary>
/// Shows and collapses the results pane inside the editor/results split. When collapsed the editor fills the
/// whole workspace — there is no empty half-split — and the proportions the user dragged are remembered, so
/// re-showing the pane restores their sizes instead of snapping back to the 2:3 default.
/// </summary>
public sealed class ResultsPaneController
{
    private readonly Grid _workspace;
    private readonly Control _splitter;
    private readonly ResultView _results;

    // Remembered editor/results split proportions (the 2:3 default until the user drags).
    private GridLength _savedEditorRow = new(2, GridUnitType.Star);
    private GridLength _savedResultsRow = new(3, GridUnitType.Star);

    // A render put off while the pane was collapsed (inline mode), run when it is next shown; and whether it
    // has anything to show, which is what Toggle asks.
    private Action? _owed;
    private bool _owedHasResults;

    public ResultsPaneController(Grid workspace, Control splitter, ResultView results)
    {
        _workspace = workspace;
        _splitter = splitter;
        _results = results;
    }

    /// <summary>
    /// Render the pane through <paramref name="render"/> — now when it is shown, or when <paramref name="defer"/>
    /// is false; otherwise <paramref name="clear"/> now and <paramref name="render"/> when it is next shown. In
    /// inline mode the results are drawn in their zones and the pane stays collapsed, and building every grid a
    /// second time for a pane nobody sees was work on the UI thread for nothing. Cleared rather than left
    /// stale, so a grid command routed to the pane can't act on an earlier run's sets.
    /// </summary>
    public void Render(Action render, Action clear, bool hasResults, bool defer)
    {
        if (defer && !IsVisible)
        {
            _owed = render;
            _owedHasResults = hasResults;
            clear();
            return;
        }
        _owed = null;
        render();
    }

    /// <summary>
    /// Render <paramref name="tab"/>'s current result frame and back-bar state, then show or collapse the pane
    /// as <paramref name="visible"/> says (null leaves it as it is). Built only when it will be seen: a collapsed
    /// pane is rendered when it is next shown.
    /// </summary>
    public void ShowTab(EditorTabViewModel? tab, bool? visible)
    {
        Render(
            () =>
            {
                _results.CanGoBack = tab?.CanGoBack ?? false;
                _results.Results = tab?.Results;   // assignment triggers the rebuild (reads CanGoBack)
            },
            () =>
            {
                _results.CanGoBack = false;
                _results.Results = null;
            },
            hasResults: tab?.Results is { Count: > 0 },
            defer: visible != true);
        if (visible is { } shown)
        {
            SetVisible(shown);
        }
    }

    /// <summary>Whether the pane is currently shown.</summary>
    public bool IsVisible => _results.IsVisible;

    /// <summary>Show or collapse the results pane (grid row + splitter). Called on every run / tab switch and
    /// by the view.toggleResults command.</summary>
    public void SetVisible(bool visible)
    {
        var rows = _workspace.RowDefinitions;
        if (visible && _owed is { } owed)
        {
            _owed = null;
            owed();
        }
        if (visible)
        {
            rows[0].Height = _savedEditorRow;
            rows[1].Height = GridLength.Auto;
            rows[2].Height = _savedResultsRow;
        }
        else
        {
            // Capture the current drag before zeroing the rows — but only if the pane was actually open,
            // or a second collapse would memorize the collapsed (zero) sizes.
            if (rows[2].Height.Value > 0) { _savedEditorRow = rows[0].Height; _savedResultsRow = rows[2].Height; }
            rows[0].Height = new GridLength(1, GridUnitType.Star); // editor fills
            rows[1].Height = new GridLength(0);
            rows[2].Height = new GridLength(0);
        }
        _splitter.IsVisible = visible;
        _results.IsVisible = visible;
    }

    /// <summary>view.toggleResults (Ctrl+R): flip the pane, but only when there is actually a result to show.
    /// Returns true when the pane ended up hidden, so the caller can pull focus back to the editor (it may
    /// have been sitting in the now-collapsed grid).</summary>
    public bool Toggle()
    {
        if (_results.Results is not { Count: > 0 } && !(_owed is not null && _owedHasResults))
        {
            return false;
        }
        var show = !_results.IsVisible;
        SetVisible(show);
        return !show;
    }
}
