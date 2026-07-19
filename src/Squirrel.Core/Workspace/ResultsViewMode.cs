namespace Squirrel.Core.Workspace;

/// <summary>
/// How a run's multiple result sets are presented in the results dock (design RESULTS_GRID §2).
/// Persisted per-user as a session preference.
/// </summary>
public enum ResultsViewMode
{
    /// <summary>Result sets stack vertically in one scroll area, each with its own meta row.</summary>
    Stacked,

    /// <summary>One result set visible at a time; the others are selectable tabs.</summary>
    Tabbed,
}
