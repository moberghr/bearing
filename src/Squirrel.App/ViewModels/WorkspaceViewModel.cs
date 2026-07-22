using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.App.Workspace;
using Squirrel.Core.Workspace;

namespace Squirrel.App.ViewModels;

/// <summary>
/// The workspace concern: the editor-tab list and selection (owned in <see cref="WorkspaceContext"/>),
/// their lifecycle (new/close/session-restore), and the tab-bridging that opens/loads/saves a script into
/// a tab. Extracted from the shell (docs/mvvm-refactor-plan.md phase 4); coordinates through the context
/// and calls back to the connections concern (connection-display) and the shell (title/scripts refresh)
/// rather than referencing those view-models directly. The shell re-exposes this VM's surface as thin
/// delegates so existing bindings, code-behind, and tests stay unchanged.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly WorkspaceContext _ctx;
    private readonly Action _refreshScripts;
    private readonly Action _updateTitle;
    private readonly Action<EditorTabViewModel> _applyConnectionDisplay;
    private readonly Func<string, string, Task> _renameScript;
    private int _scratchCounter;

    public WorkspaceViewModel(WorkspaceContext ctx, Action refreshScripts, Action updateTitle,
        Action<EditorTabViewModel> applyConnectionDisplay, Func<string, string, Task> renameScript)
    {
        _ctx = ctx;
        _refreshScripts = refreshScripts;
        _updateTitle = updateTitle;
        _applyConnectionDisplay = applyConnectionDisplay;
        _renameScript = renameScript;
        // Re-raise the binding notification when the selection changes underneath us (the context is the
        // single owner; the connections concern also listens to the same event).
        _ctx.SelectedTabChanged += () => OnPropertyChanged(nameof(SelectedTab));
    }

    /// <summary>The open editor tabs (bound as the tab strip's ItemsSource).</summary>
    public ObservableCollection<EditorTabViewModel> Tabs => _ctx.Tabs;

    /// <summary>The active editor tab (two-way binding target for the tab strip's SelectedItem).</summary>
    public EditorTabViewModel? SelectedTab
    {
        get => _ctx.SelectedTab;
        set => _ctx.SelectedTab = value;
    }

    // ---- Tab lifecycle -----------------------------------------------------------------------

    public EditorTabViewModel NewTab(string text = "", string? scriptPath = null)
    {
        var inherit = SelectedTab?.ConnectionId ?? _ctx.DefaultConnectionId;
        var tab = new EditorTabViewModel($"Scratch {++_scratchCounter}", text, scriptPath)
        {
            ConnectionId = inherit,
        };
        _applyConnectionDisplay(tab);
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

    public async Task RestoreTabsAsync(SessionState? session)
    {
        Tabs.Clear();
        _scratchCounter = 0;

        var editors = session?.OpenEditors ?? new List<OpenEditor>();
        foreach (var e in editors)
        {
            var abs = e.ScriptPath is { } rel && _ctx.Project is not null ? Path.Combine(_ctx.Project.Directory, rel) : null;
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
            tab.ConnectionId = e.ConnectionId ?? _ctx.DefaultConnectionId;
        }

        if (Tabs.Count == 0)
            NewTab();

        var idx = session?.SelectedEditorIndex ?? 0;
        SelectedTab = Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)];
    }

    // ---- Tab-bridging (open/load/save/rename a script into an editor tab) ---------------------

    /// <summary>Open a saved script: focus its existing tab, or load it into a new one.</summary>
    public async Task OpenScriptInNewTabAsync(string absolutePath)
    {
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.ScriptPath, absolutePath, StringComparison.Ordinal));
        if (existing is not null) { SelectedTab = existing; return; }
        var text = await File.ReadAllTextAsync(absolutePath, CancellationToken.None);
        NewTab(text, absolutePath);
        _ctx.SetStatus($"Opened {Path.GetFileName(absolutePath)}.");
    }

    public async Task LoadScriptIntoSelectedAsync(string absolutePath)
    {
        var text = await File.ReadAllTextAsync(absolutePath, CancellationToken.None);
        var tab = SelectedTab ?? NewTab();
        tab.Text = text;
        tab.ScriptPath = absolutePath;
        tab.MarkSaved(text);
        _refreshScripts();
        _updateTitle();
        _ctx.SetStatus($"Opened {Path.GetFileName(absolutePath)}.");
    }

    public async Task SaveSelectedScriptAsync(string absolutePath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, text, CancellationToken.None);
        var tab = SelectedTab ?? NewTab(text);
        tab.Text = text;
        tab.ScriptPath = absolutePath;
        tab.MarkSaved(text);
        _refreshScripts();
        _updateTitle();
        _ctx.SetStatus($"Saved {Path.GetFileName(absolutePath)}.");
    }

    /// <summary>Rename the selected tab: a scratch label, or the backing .sql file on disk (via scripts).</summary>
    public async Task RenameTabAsync(EditorTabViewModel tab, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (tab.IsScratch) tab.DisplayName = newName.Trim();
        else if (tab.ScriptPath is { } path) await _renameScript(path, newName.Trim());
    }
}
