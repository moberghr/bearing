using System;
using System.Collections.Generic;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;

namespace Bearing.App.Workspace;

/// <summary>
/// Everything one project keeps while it is <b>not</b> on screen. Switching projects parks the current
/// project here and unparks the target — the tabs are the same view-model instances throughout, so their
/// results, FK-navigation history, pending inline edits and in-flight queries survive the switch. A project
/// is only ever torn down by closing its tabs or quitting the app.
/// </summary>
/// <remarks>
/// <see cref="ParkedTabs"/> is populated <i>only</i> while this project is inactive: the active project's
/// tabs live in <see cref="WorkspaceContext.Tabs"/>. That invariant is what makes
/// <see cref="WorkspaceContext.AllTabs"/> an exact union with no de-duplication.
/// </remarks>
public sealed class ProjectWorkspace
{
    /// <summary>The loaded project. Settable so re-opening the same directory adopts the fresh instance
    /// without losing the tabs parked against it (see <see cref="WorkspaceContext.GetOrAdd"/>).</summary>
    public required Project Project { get; set; }

    /// <summary>This project's editor tabs while it is parked (empty while it is the active project).</summary>
    public List<EditorTabViewModel> ParkedTabs { get; } = new();

    /// <summary>Index into <see cref="ParkedTabs"/> of the tab that was selected when the project was parked.</summary>
    public int SelectedIndex { get; set; }

    /// <summary>The connection new tabs in this project fall back to (<see cref="WorkspaceContext.DefaultConnectionId"/>
    /// at park time), so switching back doesn't have to re-guess it from the manifest.</summary>
    public Guid? DefaultConnectionId { get; set; }

    // Side-pane and results layout are per-project (they round-trip through the project's session.json),
    // so they are parked alongside the tabs rather than re-read from disk on every switch back.
    public bool SidePaneOpen { get; set; } = true;
    public double SidePaneWidth { get; set; } = 260;
    public ResultsViewMode ResultsViewMode { get; set; } = ResultsViewMode.Stacked;
}
