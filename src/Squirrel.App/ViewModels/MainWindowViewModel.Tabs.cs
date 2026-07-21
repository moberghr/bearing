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
    // ---- Tabs --------------------------------------------------------------------------------

    public EditorTabViewModel NewTab(string text = "", string? scriptPath = null)
    {
        var inherit = SelectedTab?.ConnectionId ?? DefaultConnectionId;
        var tab = new EditorTabViewModel($"Scratch {++_scratchCounter}", text, scriptPath)
        {
            ConnectionId = inherit,
        };
        ApplyConnectionDisplay(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    public void CloseTab(EditorTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.Remove(tab);
        if (Tabs.Count == 0) { NewTab(); return; }
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    private async Task RestoreTabsAsync(SessionState? session)
    {
        Tabs.Clear();
        _scratchCounter = 0;

        var editors = session?.OpenEditors ?? new List<OpenEditor>();
        foreach (var e in editors)
        {
            var abs = e.ScriptPath is { } rel && _project is not null ? Path.Combine(_project.Directory, rel) : null;
            EditorTabViewModel tab;
            if (abs is not null && File.Exists(abs))
            {
                // Restore the last editor buffer (which may hold unsaved edits), keeping the on-disk
                // content as the clean baseline so an unsaved script comes back marked modified.
                var disk = await File.ReadAllTextAsync(abs, CancellationToken.None);
                var buffer = e.ScratchText ?? disk;
                tab = NewTab(buffer, abs);
                tab.MarkSaved(disk);
                tab.CaretOffset = Math.Clamp(e.CaretOffset, 0, buffer.Length);
            }
            else
            {
                tab = NewTab(e.ScratchText ?? "");
                tab.CaretOffset = Math.Clamp(e.CaretOffset, 0, tab.Text.Length);
                if (e.ScratchName is { Length: > 0 } name) tab.DisplayName = name;
            }
            tab.ConnectionId = e.ConnectionId ?? DefaultConnectionId;
        }

        if (Tabs.Count == 0)
            NewTab();

        var idx = session?.SelectedEditorIndex ?? 0;
        SelectedTab = Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)];
    }

    partial void OnSelectedTabChanged(EditorTabViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedTabConnection));
        OnPropertyChanged(nameof(ActiveConnectionColor));
        OnPropertyChanged(nameof(SelectedTabDatabase));
        IsConnected = value?.ConnectionId is { } id && _sessions.TryGet(id) is not null;
        RefreshTabDatabases(value);
        WarmConnection(value);
    }
}
