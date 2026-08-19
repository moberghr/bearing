using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Connections;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Sql;

namespace Bearing.App.ViewModels;

public sealed partial class ShellViewModel
{
    // ---- Session persistence (synchronous; safe on the close path) ---------------------------

    /// <summary>
    /// Write <c>session.json</c> for <b>every</b> open project, not just the one on screen. Projects stay
    /// open across a switch (their tabs are parked, not closed), so a shutdown or switch that only saved
    /// the active project would silently drop the others' tab lists.
    /// </summary>
    public void SaveWorkspace()
    {
        if (_project is null) return;
        _workspace.FlushScratchBlocking();   // land the last keystrokes in the scratch files themselves

        // The active project's tabs are in the live list; every other open project's are parked. Each save
        // is independently best-effort (§5.2) so one unwritable project can't skip the rest.
        Save(_project, BuildSession(_project, _workspace.Tabs, IndexOf(_workspace.SelectedTab),
            _ctx.DefaultConnectionId, SidePaneOpen, SidePaneWidth, ResultsViewMode));

        foreach (var parked in _ctx.OpenProjects)
        {
            if (ReferenceEquals(parked.Project, _project)) continue;
            Save(parked.Project, BuildSession(parked.Project, parked.ParkedTabs, parked.SelectedIndex,
                parked.DefaultConnectionId, parked.SidePaneOpen, parked.SidePaneWidth, parked.ResultsViewMode));
        }

        void Save(Project project, SessionState state)
        {
            try { _sessionStore.Save(project.Directory, state); }
            catch { /* best-effort on shutdown */ }
        }
    }

    private int IndexOf(EditorTabViewModel? tab)
        => tab is null ? 0 : Math.Max(0, _workspace.Tabs.IndexOf(tab));

    private static SessionState BuildSession(
        Project project,
        IReadOnlyList<EditorTabViewModel> tabs,
        int selectedIndex,
        Guid? defaultConnectionId,
        bool sidePaneOpen,
        double sidePaneWidth,
        ResultsViewMode resultsViewMode)
    {
        var editors = tabs.Select(t => new OpenEditor
        {
            ScriptPath = t.ScriptPath is not null ? Path.GetRelativePath(project.Directory, t.ScriptPath) : null,
            ScratchText = t.Text,
            // Only a label the user typed on a tab with no file is worth carrying: a tab with a file is
            // named after that file (#1), and the "Scratch N" placeholder is regenerated on restore.
            ScratchName = t.ScriptPath is null && t.IsUserNamed ? t.DisplayName : null,
            CaretOffset = t.CaretOffset,
            ConnectionId = t.ConnectionId,
        }).ToList();

        var selected = selectedIndex >= 0 && selectedIndex < tabs.Count ? tabs[selectedIndex] : null;

        return new SessionState
        {
            ActiveConnectionId = selected?.ConnectionId ?? defaultConnectionId,
            OpenEditors = editors,
            SelectedEditorIndex = Math.Max(0, selectedIndex),
            LastOpenedUtc = DateTime.UtcNow.ToString("o"),
            SidePaneOpen = sidePaneOpen,
            SidePaneWidth = sidePaneWidth,
            ResultsViewMode = resultsViewMode,
        };
    }

    private void UpdateTitle()
        => Title = _project is null ? "Bearing" : $"Bearing — {_project.Manifest.Name}";
}
