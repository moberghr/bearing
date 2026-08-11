using Avalonia.Controls;
using Bearing.App.Controls;

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

    public ResultsPaneController(Grid workspace, Control splitter, ResultView results)
    {
        _workspace = workspace;
        _splitter = splitter;
        _results = results;
    }

    /// <summary>Whether the pane is currently shown.</summary>
    public bool IsVisible => _results.IsVisible;

    /// <summary>Show or collapse the results pane (grid row + splitter). Called on every run / tab switch and
    /// by the view.toggleResults command.</summary>
    public void SetVisible(bool visible)
    {
        var rows = _workspace.RowDefinitions;
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
        if (_results.Results is not { Count: > 0 }) return false;
        var show = !_results.IsVisible;
        SetVisible(show);
        return !show;
    }
}
