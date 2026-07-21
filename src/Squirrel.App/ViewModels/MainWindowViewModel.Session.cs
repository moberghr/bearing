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
using Squirrel.App.Connections;
using Squirrel.App.Formatting;
using Squirrel.App.Results;
using Squirrel.Core.Data;
using Squirrel.Core.Logging;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;
using Squirrel.Sql;

namespace Squirrel.App.ViewModels;
public sealed partial class MainWindowViewModel
{
    // ---- Session persistence (synchronous; safe on the close path) ---------------------------

    public void SaveWorkspace()
    {
        if (_project is null) return;
        try { _sessionStore.Save(_project.Directory, BuildSession()); }
        catch { /* best-effort on shutdown */ }
    }

    private SessionState BuildSession()
    {
        var editors = Tabs.Select(t => new OpenEditor
        {
            ScriptPath = t.ScriptPath is not null && _project is not null
                ? Path.GetRelativePath(_project.Directory, t.ScriptPath)
                : null,
            ScratchText = t.Text,
            ScratchName = t.IsScratch ? t.DisplayName : null,
            CaretOffset = t.CaretOffset,
            ConnectionId = t.ConnectionId,
        }).ToList();

        return new SessionState
        {
            ActiveConnectionId = SelectedTab?.ConnectionId ?? DefaultConnectionId,
            OpenEditors = editors,
            SelectedEditorIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab)),
            LastOpenedUtc = DateTime.UtcNow.ToString("o"),
            SidePaneOpen = SidePaneOpen,
            SidePaneWidth = SidePaneWidth,
            ResultsViewMode = ResultsViewMode,
        };
    }

    private void UpdateTitle()
        => Title = _project is null ? "Squirrel" : $"Squirrel — {_project.Manifest.Name}";
}
