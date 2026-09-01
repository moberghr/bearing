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
    private readonly TabAutosave _autosave;
    private int _scratchCounter;

    public WorkspaceViewModel(WorkspaceContext ctx, ScriptsViewModel scripts, ConnectionsViewModel connections,
        IDialogService? dialogs = null, TabAutosave? autosave = null)
    {
        _ctx = ctx;
        _scripts = scripts;
        _connections = connections;
        _dialogs = dialogs;
        _autosave = autosave ?? new TabAutosave(ctx);
        _ctx.Autosave = _autosave;   // the execution concern signals runs through the context
        // A new scratch file appears in the tree; updates to an existing one don't move anything.
        _autosave.FileCreated += _scripts.RefreshScripts;
        // Re-raise the binding notification when the selection changes underneath us (the context is the
        // single owner; the connections concern also listens to the same event).
        _ctx.SelectedTabChanged += () =>
        {
            OnPropertyChanged(nameof(SelectedTab));
            OnPropertyChanged(nameof(SelectionIsPinned));
        };
        // Hooked once here rather than called at each Tabs mutation: opening, closing, restoring and a
        // project switch all move tabs, and a split that has to be remembered at five call sites is a split
        // that will be forgotten at the sixth.
        ((System.Collections.Specialized.INotifyCollectionChanged)Tabs).CollectionChanged += (_, _) => ResplitTabs();
    }

    /// <summary>The autosave coordinator. Also reached by the execution concern through
    /// <see cref="WorkspaceContext.Autosave"/> to signal a run for <see cref="AutosaveMode.OnExecute"/>.</summary>
    public TabAutosave Autosave => _autosave;

    /// <summary>Write any pending buffers now — the project-switch path, where a debounced write would
    /// otherwise be dropped when the tab list is cleared.</summary>
    public Task FlushScratchAsync() => _autosave.FlushAllAsync();

    /// <summary>The shutdown counterpart: synchronous, and only for tabs that already have a file.
    /// See <see cref="TabAutosave.FlushExistingBlocking"/> for why it's narrower.</summary>
    public void FlushScratchBlocking() => _autosave.FlushExistingBlocking();

    /// <summary>The active project's editor tabs (bound as the tab strip's ItemsSource).</summary>
    public ObservableCollection<EditorTabViewModel> Tabs => _ctx.Tabs;

    /// <summary>Every live tab across every open project — parked ones included. Tab navigation and the
    /// strip deliberately stay inside <see cref="Tabs"/>; this is for the things that must not lose sight
    /// of a tab just because its project isn't showing (the quit guard, background-run reporting).</summary>
    public IEnumerable<EditorTabViewModel> AllTabs => _ctx.AllTabs;

    /// <summary>The project that owns a tab's files — its own, not whichever is active (tabs outlive a
    /// project switch). Falls back to the active project for a tab created before any project loaded.</summary>
    private Project? ProjectOf(EditorTabViewModel tab) => _ctx.ProjectOf(tab) ?? _ctx.Project;

    /// <summary>The active editor tab (two-way binding target for the tab strip's SelectedItem).</summary>
    public EditorTabViewModel? SelectedTab
    {
        get => _ctx.SelectedTab;
        set => _ctx.SelectedTab = value;
    }

    // ---- Tab lifecycle -----------------------------------------------------------------------

    /// <summary>Open a tab. With no <paramref name="scriptPath"/> it's a scratch buffer, which autosave
    /// backs with a real file in the scratch folder as soon as it has content.
    /// <para>
    /// The <c>Scratch N</c> placeholder is only minted for a tab that has no file to be named after, so the
    /// counter no longer skips numbers every time a saved script is opened.
    /// </para></summary>
    /// <param name="connectionId">Point the new tab at this connection instead of inheriting one (#57 —
    /// "new script using this connection"). Inheritance is still the default: a tab opened from the editor
    /// keeps working against whatever the last one did.</param>
    public EditorTabViewModel NewTab(string text = "", string? scriptPath = null, Guid? connectionId = null)
    {
        var inherit = connectionId ?? SelectedTab?.ConnectionId ?? _ctx.DefaultConnectionId;
        var isScratch = scriptPath is null || ScratchNaming.IsUnderScratch(scriptPath, _ctx.Project?.ScratchDirectory);
        var label = scriptPath is null
            ? $"Scratch {++_scratchCounter}"
            : Path.GetFileNameWithoutExtension(scriptPath);
        var tab = new EditorTabViewModel(label, text, scriptPath, isScratch)
        {
            ConnectionId = inherit,
            ProjectDirectory = _ctx.Project?.Directory,   // fixed for the tab's life; survives project switches
        };
        _connections.ApplyConnectionDisplay(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>
    /// Close a tab, asking first when that would lose work — a query still running on it, or unsaved text
    /// (<see cref="EditorTabViewModel.HasUnsavedWork"/>). Returns true when the tab actually closed — false
    /// means the user cancelled, or a save they asked for didn't happen, and the tab is still open. Every
    /// close path funnels through here (the Ctrl+F4 command, the File menu, and the tab strip's ✕) so the
    /// prompts can't be routed around.
    /// <para>
    /// Quitting and switching projects deliberately do <b>not</b> come through here: both call
    /// <c>SaveWorkspace</c> first, which round-trips every buffer — scratch text included — through
    /// <c>session.json</c>, so nothing is lost and a prompt would be pure friction.
    /// </para>
    /// </summary>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
        if (!Tabs.Contains(tab)) return false;

        // A query in flight is work in progress too, and closing its tab would abandon it. Asked before the
        // unsaved-work prompt: no point deciding what to save on a tab the user then chooses to keep. Not
        // gated on ConfirmTabClose — that setting is about discarding *text*, which the user can weigh up
        // by looking at it; a running query's cost isn't visible the same way.
        if (tab.IsRunning && _dialogs is { } runDialogs)
        {
            if (!await runDialogs.ConfirmCancelRunningAsync(1, tab.Header)) return false;
            tab.CancelRun();
        }

        // Land any debounced write before deciding: a tab whose text already reached its file has nothing
        // to lose and must close without a prompt. FlushAsync itself respects the autosave mode, so this
        // can't quietly save a named script the user asked never to autosave.
        await _autosave.FlushAsync(tab);

        // ConfirmTabClose off means the user has accepted that a close discards unsaved work — the one
        // setting whose whole purpose is to remove a safety net, so it gates only the prompt, never the
        // autosave flush above.
        if (tab.HasUnsavedWork && _ctx.Settings.ConfirmTabClose && _dialogs is { } dialogs)
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
        var path = tab.ScriptPath ?? await dialogs.PickSaveScriptAsync($"{tab.DisplayName}.sql", ProjectOf(tab)?.ScriptsDirectory);
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
    /// <summary>
    /// Drop a tab and land the selection on its left-hand neighbour (its right-hand one when it was the
    /// leftmost). Closing a run of tabs then keeps walking left, instead of stopping dead the moment the
    /// selection reaches the end of the strip.
    /// <para>
    /// Whether the closing tab was selected is read <b>before</b> it leaves the list, not after (#87).
    /// <see cref="SelectedTab"/> is bound two-way to the tab strip's <c>SelectedItem</c>, so removing the
    /// item makes the control fix up its own selection and write that back through the binding — by the time
    /// the old check ran, <c>SelectedTab</c> was already whatever the control had picked, the guard was
    /// false, and the workspace never chose a replacement at all. It read as correct with no view attached,
    /// which is exactly why it shipped, so the test below fakes a control that does the same write.
    /// </para>
    /// </summary>
    private void Remove(EditorTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        var wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.Remove(tab);
        if (Tabs.Count == 0) { NewTab(); return; }

        // Also re-selects when the control left SelectedTab on a tab that is no longer in the list.
        if (wasSelected || SelectedTab is null || !Tabs.Contains(SelectedTab))
            SelectedTab = Tabs[Math.Max(0, index - 1)];
    }

    /// <summary>
    /// The pinned tabs and the rest, in <see cref="Tabs"/> order — two live views over the one source, so the
    /// strip can render pinned tabs in their own row (#67) while every index-based rule keeps reading
    /// <see cref="Tabs"/>.
    /// <para>
    /// Kept in step by minimal edits rather than rebuilt: the strips bind their selection two-way, so a
    /// <c>Clear()</c> would make the control fix up its own selection and write that back — the shape of
    /// #87, and the reason these are patched in place.
    /// </para>
    /// </summary>
    public ObservableCollection<EditorTabViewModel> PinnedTabs { get; } = new();

    /// <inheritdoc cref="PinnedTabs"/>
    public ObservableCollection<EditorTabViewModel> UnpinnedTabs { get; } = new();

    /// <summary>
    /// Whether the selected tab is in the pinned row. The strips read this to decide which of them draws a
    /// selection at all: a <c>TabStrip</c> is always-selected, so the row that does not hold the selected tab
    /// still has one of its own, and without this both rows would look selected (#67).
    /// </summary>
    public bool SelectionIsPinned => SelectedTab?.IsPinned == true;

    /// <summary>True while any tab is pinned — the pinned row is hidden entirely when none is, rather than
    /// sitting there as an empty strip.</summary>
    public bool HasPinnedTabs => PinnedTabs.Count > 0;

    /// <summary>Pin or unpin, and re-split the views. Public because the context menu, the command and the
    /// session restore all reach for it.</summary>
    public void SetPinned(EditorTabViewModel tab, bool pinned)
    {
        if (!Tabs.Contains(tab) || tab.IsPinned == pinned) return;
        tab.IsPinned = pinned;
        ResplitTabs();
    }

    /// <summary>Re-derive <see cref="PinnedTabs"/> and <see cref="UnpinnedTabs"/> from <see cref="Tabs"/>.</summary>
    private void ResplitTabs()
    {
        TabViewSync.Apply(PinnedTabs, Tabs.Where(t => t.IsPinned).ToList());
        TabViewSync.Apply(UnpinnedTabs, Tabs.Where(t => !t.IsPinned).ToList());
        OnPropertyChanged(nameof(HasPinnedTabs));
        OnPropertyChanged(nameof(SelectionIsPinned));
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
                // No label to re-apply: a tab with a file is named after that file (#1). Older sessions
                // still carry a ScratchName for these; it described a header that no longer exists.
            }
            else
            {
                // No file: either a pre-Phase-2 session with inlined scratch text, or a scratch file that
                // has since been deleted. Either way it comes back as a scratch buffer and autosave will
                // give it a fresh file on the next keystroke.
                tab = NewTab(e.ScratchText ?? "");
                tab.CaretOffset = Math.Clamp(e.CaretOffset, 0, tab.Text.Length);
                if (e.ScratchName is { Length: > 0 } name)
                {
                    tab.DisplayName = name;
                    // A placeholder from an older session must not go on to name a file (#1).
                    tab.IsUserNamed = !ScratchNaming.IsGeneratedLabel(name);
                }
            }
            tab.ConnectionId = e.ConnectionId ?? _ctx.DefaultConnectionId;
            tab.IsPinned = e.Pinned;
        }

        if (Tabs.Count == 0)
            NewTab();

        ResplitTabs();
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
        tab.IsScratch = ScratchNaming.IsUnderScratch(tab.ScriptPath, ProjectOf(tab)?.ScratchDirectory);
    }

    /// <summary>
    /// Delete a script file and close the tabs that were showing it. The caller has already confirmed.
    /// <para>
    /// Order matters: any pending autosave is <b>discarded</b> first, or the debounced write would recreate
    /// the file moments after it was deleted. The tabs then close without the unsaved-work prompt — there is
    /// nothing left to save them to, and asking would be offering to write the file back.
    /// </para>
    /// </summary>
    /// <returns>False when the delete failed, leaving the tabs open.</returns>
    public bool DeleteScript(string absolutePath)
    {
        var backing = AllTabs.Where(t => string.Equals(t.ScriptPath, absolutePath, StringComparison.Ordinal)).ToList();
        foreach (var tab in backing) _autosave.Discard(tab);

        if (!_scripts.DeleteScript(absolutePath)) return false;

        foreach (var tab in backing)
        {
            tab.CancelRun();
            Remove(tab);   // no prompt: the file it would save to is gone
        }
        return true;
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
        tab.IsUserNamed = true;            // if it has no file yet, this label is what autosave will name it
        if (tab.ScriptPath is not { } path || ProjectOf(tab) is not { } project) return;

        await _scripts.RenameScriptAsync(path, newName, project.ScriptsDirectory);
        // Only leave scratch if the move actually happened (a name clash leaves the file where it was).
        tab.IsScratch = ScratchNaming.IsUnderScratch(tab.ScriptPath, project.ScratchDirectory);
    }
}
