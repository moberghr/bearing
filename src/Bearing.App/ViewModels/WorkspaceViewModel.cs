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
    private readonly ScratchAutosave _autosave;
    private int _scratchCounter;

    public WorkspaceViewModel(WorkspaceContext ctx, ScriptsViewModel scripts, ConnectionsViewModel connections,
        IDialogService? dialogs = null, ScratchAutosave? autosave = null)
    {
        _ctx = ctx;
        _scripts = scripts;
        _connections = connections;
        _dialogs = dialogs;
        _autosave = autosave ?? new ScratchAutosave(ctx);
        // A new scratch file appears in the tree; updates to an existing one don't move anything.
        _autosave.Saved += _scripts.RefreshScripts;
        // Re-raise the binding notification when the selection changes underneath us (the context is the
        // single owner; the connections concern also listens to the same event).
        _ctx.SelectedTabChanged += () => OnPropertyChanged(nameof(SelectedTab));
    }

    /// <summary>Write any pending scratch buffers now — the project-switch path, where a debounced write
    /// would otherwise be dropped when the tab list is cleared.</summary>
    public Task FlushScratchAsync() => _autosave.FlushAllAsync();

    /// <summary>The shutdown counterpart: synchronous, and only for scratch tabs that already have a file.
    /// See <see cref="ScratchAutosave.FlushExistingBlocking"/> for why it's narrower.</summary>
    public void FlushScratchBlocking() => _autosave.FlushExistingBlocking();

    /// <summary>The open editor tabs (bound as the tab strip's ItemsSource).</summary>
    public ObservableCollection<EditorTabViewModel> Tabs => _ctx.Tabs;

    /// <summary>The active editor tab (two-way binding target for the tab strip's SelectedItem).</summary>
    public EditorTabViewModel? SelectedTab
    {
        get => _ctx.SelectedTab;
        set => _ctx.SelectedTab = value;
    }

    // ---- Tab lifecycle -----------------------------------------------------------------------

    /// <summary>Open a tab. With no <paramref name="scriptPath"/> it's a scratch buffer, which autosave
    /// backs with a real file in the scratch folder as soon as it has content.</summary>
    public EditorTabViewModel NewTab(string text = "", string? scriptPath = null)
    {
        var inherit = SelectedTab?.ConnectionId ?? _ctx.DefaultConnectionId;
        var isScratch = scriptPath is null || ScratchNaming.IsUnderScratch(scriptPath, _ctx.Project?.ScratchDirectory);
        var tab = new EditorTabViewModel($"Scratch {++_scratchCounter}", text, scriptPath, isScratch)
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

        // Land any debounced scratch write before deciding: a scratch tab whose text reached its file has
        // nothing to lose and must close without a prompt.
        if (tab.IsScratch) await _autosave.FlushAsync(tab);

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
                tab = NewTab(buffer, abs);   // NewTab re-derives IsScratch from where the file lives
                tab.MarkSaved(disk);
                tab.CaretOffset = Math.Clamp(e.CaretOffset, 0, buffer.Length);
                // A scratch file keeps its label rather than showing its generated filename.
                if (tab.IsScratch && e.ScratchName is { Length: > 0 } label) tab.DisplayName = label;
            }
            else
            {
                // No file: either a pre-Phase-2 session with inlined scratch text, or a scratch file that
                // has since been deleted. Either way it comes back as a scratch buffer and autosave will
                // give it a fresh file on the next keystroke.
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
        SyncScratchFlag(tab);
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
        SyncScratchFlag(tab);   // saving somewhere the user chose promotes the tab out of scratch
        tab.MarkSaved(text);
        _scripts.RefreshScripts();
        _ctx.SetStatus($"Saved {Path.GetFileName(absolutePath)}.");
    }

    /// <summary>Re-derive scratch membership from where the tab's file now lives. Anything that repoints
    /// <see cref="EditorTabViewModel.ScriptPath"/> must call this — a buffer is scratch because of the
    /// folder it sits in, so a save, load, or move can promote it (or, moving the other way, demote it).</summary>
    private void SyncScratchFlag(EditorTabViewModel tab)
    {
        if (tab.ScriptPath is null) return;   // no file yet: still an unnamed scratch buffer
        tab.IsScratch = ScratchNaming.IsUnderScratch(tab.ScriptPath, _ctx.Project?.ScratchDirectory);
    }

    /// <summary>
    /// Rename a tab. For a named script that's a file rename in place. For a scratch tab it's a
    /// <b>promotion</b>: the pending buffer is flushed, then its file moves out of the scratch folder to
    /// the scripts root under the new name — naming a scratch buffer is what makes it a curated script.
    /// An empty scratch tab has no file to promote, so it just takes the new label and stays scratch.
    /// </summary>
    public async Task RenameTabAsync(EditorTabViewModel tab, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        newName = newName.Trim();

        if (!tab.IsScratch)
        {
            if (tab.ScriptPath is { } named) await _scripts.RenameScriptAsync(named, newName);
            return;
        }

        await _autosave.FlushAsync(tab);   // make sure there's a file to move, and that it's current
        tab.DisplayName = newName;
        if (tab.ScriptPath is not { } path || _ctx.Project is not { } project) return;

        await _scripts.RenameScriptAsync(path, newName, project.ScriptsDirectory);
        // Only leave scratch if the move actually happened (a name clash leaves the file where it was).
        tab.IsScratch = ScratchNaming.IsUnderScratch(tab.ScriptPath, project.ScratchDirectory);
    }
}
