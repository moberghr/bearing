namespace Bearing.Core.Workspace;

/// <summary>
/// Where a run's result sets are shown: the results dock below the editor, or inline in the editor itself,
/// directly beneath the statement that produced them.
/// </summary>
public enum ResultsPlacement
{
    /// <summary>The results pane below the editor (Stacked or Tabbed, <see cref="ResultsViewMode"/>).</summary>
    Pane,

    /// <summary>Under the statement, inside the editor's scroll: the same results view as the pane (editing,
    /// foreign-key navigation, export), one per zone. The pane does not open by itself.</summary>
    Inline,
}
