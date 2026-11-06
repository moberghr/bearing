namespace Bearing.Core.Workspace;

/// <summary>
/// Where a run's result sets are shown: the results dock below the editor, or inline in the editor itself,
/// directly beneath the statement that produced them.
/// </summary>
public enum ResultsPlacement
{
    /// <summary>The results pane below the editor (Stacked or Tabbed, <see cref="ResultsViewMode"/>).</summary>
    Pane,

    /// <summary>A compact read-only table under the statement, inside the editor's scroll. The pane still holds
    /// the run's results for editing, navigation and export, but does not open by itself.</summary>
    Inline,
}
