using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Services;
using Bearing.App.Workspace;
using Bearing.Core.Workspace;

namespace Bearing.App.ViewModels;

/// <summary>
/// The workspace concern: the editor-tab list and selection (owned in <see cref="WorkspaceContext"/>),
/// their lifecycle (new/close/session-restore), and the tab-bridging that opens/loads/saves a script into
/// a tab. Extracted from the shell (docs/mvvm-refactor-plan.md phase 4); coordinates through the context
/// and its two sibling concerns — <see cref="ScriptsViewModel"/> (refresh the tree, rename a file) and
/// <see cref="ConnectionsViewModel"/> (apply a tab's connection display). It holds them directly rather
/// than through a bag of loose callbacks; neither references the workspace back, so there is no cycle.
/// The shell re-exposes this VM's surface as thin delegates so existing bindings, code-behind, and tests
/// stay unchanged.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly WorkspaceContext _ctx;
    private readonly ScriptsViewModel _scripts;
    private readonly ConnectionsViewModel _connections;
    private readonly IDialogService? _dialogs;
    private int _scratchCounter;

    public WorkspaceViewModel(WorkspaceContext ctx, ScriptsViewModel scripts, ConnectionsViewModel connections,
        IDialogService? dialogs = null)
    {
        _ctx = ctx;
        _scripts = scripts;
        _connections = connections;
        _dialogs = dialogs;
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
        _connections.ApplyConnectionDisplay(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>
    /// Close a tab, asking first when that would lose work (<see cref="EditorTabViewModel.HasUnsavedWork"/>).
    /// Returns true when the tab actually closed — false means the user cancelled, or a save they asked for
    /// didn't happen, and the tab is still open. Every close path funnels through here (the Ctrl+F4 command,
    /// the File menu, and the tab strip's ✕) so the prompt can't be routed around.
    /// <para>
    /// Quitting and switching projects deliberately do <b>not</b> come through here: both call
    /// <c>SaveWorkspace</c> first, which round-trips every buffer — scratch text included — through
    /// <c>session.json</c>, so nothing is lost and a prompt would be pure friction.
    /// </para>
    /// </summary>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
        if (!Tabs.Contains(tab)) return false;

        if (tab.HasUnsavedWork && _dialogs is { } dialogs)
        {
            switch (await dialogs.ConfirmCloseTabAsync(tab.Header))
            {
                case CloseChoice.Cancel:
                    return false;
                case CloseChoice.Save when !await SaveForCloseAsync(tab, dialogs):
                    return false; // save failed, or the destination picker was dismissed — keep the tab
            }
        }

        Remove(tab);
        return true;
    }

    /// <summary>Save on the way out. A file-backed tab writes to its own path; a scratch tab has none, so
    /// it needs a destination first — dismissing that picker aborts the close rather than losing the text.
    /// Returns false if the work is still unsaved.</summary>
    private async Task<bool> SaveForCloseAsync(EditorTabViewModel tab, IDialogService dialogs)
    {
        var path = tab.ScriptPath ?? await dialogs.PickSaveScriptAsync($"{tab.DisplayName}.sql", _ctx.Project?.ScriptsDirectory);
        if (path is null) return false;

        try
        {
            await SaveScriptAsync(tab, path, tab.Text);
            return true;
        }
        catch (Exception ex)
        {
            // A failed write must not take the buffer down with it.
            _ctx.SetStatus($"Could not save {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Remove the tab and settle the selection. The workspace always keeps at least one tab.</summary>
    private void Remove(EditorTabViewModel tab)
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
            if (abs is not null && _ctx.ScriptStore.FileExists(abs))
            {
                // Restore the last editor buffer (which may hold unsaved edits), keeping the on-disk
                // content as the clean baseline so an unsaved script comes back marked modified.
                var disk = await _ctx.ScriptStore.ReadTextAsync(abs, CancellationToken.None);
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
        var text = await _ctx.ScriptStore.ReadTextAsync(absolutePath, CancellationToken.None);
        NewTab(text, absolutePath);
        _ctx.SetStatus($"Opened {Path.GetFileName(absolutePath)}.");
    }

    public async Task LoadScriptIntoSelectedAsync(string absolutePath)
    {
        var text = await _ctx.ScriptStore.ReadTextAsync(absolutePath, CancellationToken.None);
        var tab = SelectedTab ?? NewTab();
        tab.Text = text;
        tab.ScriptPath = absolutePath;
        tab.MarkSaved(text);
        _scripts.RefreshScripts();
        _ctx.SetStatus($"Opened {Path.GetFileName(absolutePath)}.");
    }

    public Task SaveSelectedScriptAsync(string absolutePath, string text)
        => SaveScriptAsync(SelectedTab ?? NewTab(text), absolutePath, text);

    /// <summary>Write <paramref name="text"/> to disk and rebind <paramref name="tab"/> to it as its clean
    /// baseline. Per-tab rather than selection-based, because closing a background tab has to save that
    /// tab, not whichever one happens to be focused.</summary>
    public async Task SaveScriptAsync(EditorTabViewModel tab, string absolutePath, string text)
    {
        await _ctx.ScriptStore.WriteTextAsync(absolutePath, text, CancellationToken.None);
        tab.Text = text;
        tab.ScriptPath = absolutePath;
        tab.MarkSaved(text);
        _scripts.RefreshScripts();
        _ctx.SetStatus($"Saved {Path.GetFileName(absolutePath)}.");
    }

    /// <summary>Rename the selected tab: a scratch label, or the backing .sql file on disk (via scripts).</summary>
    public async Task RenameTabAsync(EditorTabViewModel tab, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (tab.IsScratch) tab.DisplayName = newName.Trim();
        else if (tab.ScriptPath is { } path) await _scripts.RenameScriptAsync(path, newName.Trim());
    }
}
