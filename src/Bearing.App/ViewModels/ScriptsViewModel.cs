using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Workspace;
using Bearing.Core.Workspace;

namespace Bearing.App.ViewModels;

/// <summary>
/// The scripts concern: the scripts-folder tree (folders + .sql leaves), its name filter, and folder/file
/// CRUD (create/move/rename). Extracted from the shell (docs/mvvm-refactor-plan.md phase 2); coordinates
/// through <see cref="WorkspaceContext"/> and reads the tab list from it (for unsaved-dot marking +
/// repointing a tab's backing path). The tab-bridging open/load/save operations moved to the workspace VM
/// in phase 4. The shell re-exposes this VM's surface as thin delegates.
/// </summary>
public sealed partial class ScriptsViewModel : ObservableObject
{
    private readonly WorkspaceContext _ctx;
    private readonly Action _updateTitle;

    public ScriptsViewModel(WorkspaceContext ctx, Action updateTitle)
    {
        _ctx = ctx;
        _updateTitle = updateTitle;
    }

    private ObservableCollection<EditorTabViewModel> Tabs => _ctx.Tabs;

    /// <summary>The flat list of every script under the project (fed while building the tree).</summary>
    public ObservableCollection<ScriptItem> Scripts { get; } = new();

    /// <summary>The Scripts tree: folders (nested) then ungrouped root scripts.</summary>
    public ObservableCollection<object> ScriptNodes { get; } = new();

    /// <summary>Filter for the Scripts tree: file names always, and file <em>contents</em> once it is long
    /// enough to be worth reading them for (empty = show all).</summary>
    [ObservableProperty] private string _scriptFilter = "";

    partial void OnScriptFilterChanged(string value)
    {
        CancelContentSearch();
        // Hits belong to the filter that found them, so a changed filter invalidates them and the refresh
        // below is what takes them off screen. Content-only rows therefore blink out while you type and come
        // back when the next pass lands, which is the honest order: the tree never shows a file that doesn't
        // match what is currently typed.
        _contentHits.Clear();
        RefreshScripts();                                  // name matches, synchronously, exactly as before
        var filter = value.Trim();
        if (ScriptSearch.WantsContentSearch(filter))
            _ = SearchContentsAsync(filter, ContentSearchDelay, StartContentSearch());
    }

    // ---- content search (#47) --------------------------------------------------------------------------
    // The name filter is synchronous off the property setter, and stays that way: re-walking the directory
    // per keystroke is cheap, reading every file's text is not, and IScriptStore only offers an async read.
    // So contents are a second, debounced, cancelable pass that ends by re-running the same refresh.

    /// <summary>How long typing has to stop before contents are read. A keystroke inside the window
    /// abandons the pass in flight rather than queueing another.</summary>
    private static readonly TimeSpan ContentSearchDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>At most this many files are read per pass. A scripts folder large enough to hit the cap is
    /// one where a read per file per pass is the wrong shape anyway (an mtime-keyed text cache would be the
    /// answer); the count is surfaced rather than silently truncated.</summary>
    private const int MaxContentFiles = 400;

    /// <summary>Content hits from the last completed pass: path → the line that matched. Read by
    /// <see cref="RefreshScripts"/>, which is why a pass ends in a refresh rather than editing the tree.</summary>
    private readonly Dictionary<string, string> _contentHits = new(StringComparer.Ordinal);

    private CancellationTokenSource? _contentSearch;

    private void CancelContentSearch()
    {
        _contentSearch?.Cancel();
        _contentSearch?.Dispose();
        _contentSearch = null;
    }

    private CancellationToken StartContentSearch()
    {
        _contentSearch = new CancellationTokenSource();
        return _contentSearch.Token;
    }

    /// <summary>
    /// Look inside the scripts the name filter didn't already reach and re-run the refresh with what matched.
    /// Continuations stay on the caller's context — the UI thread in the app, because the filter setter runs
    /// there — since the refresh at the end touches observable collections.
    /// </summary>
    private async Task SearchContentsAsync(string filter, TimeSpan debounce, CancellationToken ct)
    {
        try
        {
            if (debounce > TimeSpan.Zero) await Task.Delay(debounce, ct);
            var dir = _ctx.Project?.ScriptsDirectory;
            if (dir is null || _ctx.ScriptStore.ReadTree(dir) is not { } tree) return;

            // An open tab's buffer beats the file it came from: with autosave off (or on-execute) a script
            // can sit edited indefinitely, and the text just typed is the text most likely being searched
            // for. Reading from disk here would search a version the user cannot see.
            var buffers = Tabs.Where(t => t.ScriptPath is not null)
                              .GroupBy(t => t.ScriptPath!, StringComparer.Ordinal)
                              .ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);

            var hits = new Dictionary<string, string>(StringComparer.Ordinal);
            var read = 0;
            var skipped = 0;
            foreach (var file in Candidates(tree, filter))
            {
                ct.ThrowIfCancellationRequested();
                string text;
                if (buffers.TryGetValue(file.Path, out var live)) text = live;
                else if (file.SizeBytes > ScriptSearch.MaxContentBytes) { skipped++; continue; }
                else if (read >= MaxContentFiles) { skipped++; continue; }
                else
                {
                    read++;
                    try { text = await _ctx.ScriptStore.ReadTextAsync(file.Path, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { continue; }   // unreadable file: it simply doesn't match
                }
                if (ScriptSearch.MatchingLine(text, filter) is { } line) hits[file.Path] = line;
            }

            if (ct.IsCancellationRequested) return;
            _contentHits.Clear();
            foreach (var (path, line) in hits) _contentHits[path] = line;
            RefreshScripts();
            if (skipped > 0)
                _ctx.SetStatus($"Searched script contents for “{filter}”: {hits.Count} file(s) matched, {skipped} not searched (too large, or past the {MaxContentFiles}-file cap).");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer filter (or a project switch). The refresh for that one owns the tree.
        }
    }

    /// <summary>Every script the name filter didn't already reach — a name hit is its own reason to show a
    /// row, and its own explanation, so there is nothing to gain by reading it.</summary>
    private static IEnumerable<ScriptFileRef> Candidates(ScriptTree tree, string filter)
    {
        foreach (var file in tree.Files)
            if (!ScriptSearch.MatchesName(file.Name, filter)) yield return file;
        foreach (var sub in tree.Folders)
            foreach (var file in Candidates(sub, filter)) yield return file;
    }

    /// <summary>Run a content pass now, with no debounce, and return once the tree has been refreshed with
    /// what it found. The production path is fire-and-forget by nature; tests need a pass they can await
    /// rather than race (§4.3 — none of this is reachable from a headless UI test).</summary>
    internal Task SearchContentsNowAsync(string filter, CancellationToken ct = default)
        => SearchContentsAsync(filter.Trim(), TimeSpan.Zero, ct);

    /// <summary>The folder currently painted as the drop target. Tracked so it can be un-painted when the
    /// pointer moves on — the tree is rebuilt often enough that hunting for "whichever one is lit" isn't safe.</summary>
    private ScriptFolderViewModel? _dropFolder;

    /// <summary>
    /// Show where a dragged script would land: the folder row under the pointer, or nothing at all — which is
    /// itself the mark for "this drop goes to the scripts root". Exactly one folder is ever highlighted.
    /// View-model state rather than code-behind state (§2.2), which is also the only way it can be tested (§4.3).
    /// </summary>
    public void MarkDropTarget(ScriptFolderViewModel? folder)
    {
        if (ReferenceEquals(_dropFolder, folder)) return;
        if (_dropFolder is not null) _dropFolder.IsDropTarget = false;
        _dropFolder = folder;
        if (folder is not null) folder.IsDropTarget = true;
    }

    /// <summary>The drag is over — nothing is a drop target any more.</summary>
    public void ClearDropTarget() => MarkDropTarget(null);

    /// <summary>The script currently being dragged, so it can be un-dimmed when the drag ends.</summary>
    private ScriptItem? _dragging;

    /// <summary>
    /// Mark the script being dragged (null when the drag ends). At most one is ever in flight. Paired with
    /// <see cref="MarkDropTarget"/>: between them the two answer "what am I dragging?" and "where will it
    /// go?", which is the feedback a drag cursor would have given if the platform let the app set one.
    /// </summary>
    public void MarkDragging(ScriptItem? script)
    {
        if (ReferenceEquals(_dragging, script)) return;
        if (_dragging is not null) _dragging.IsDragging = false;
        _dragging = script;
        if (script is not null) script.IsDragging = true;
    }

    /// <summary>
    /// The tree's selected node (two-way bound to the TreeView) — a <see cref="ScriptItem"/> or a
    /// <see cref="ScriptFolderViewModel"/>. Owned here rather than left to the control so the selection can
    /// be <em>set</em> from a view-model (<see cref="Reveal"/>) and can survive a refresh: the tree is
    /// rebuilt wholesale, which would otherwise drop it every time a file appears.
    /// </summary>
    [ObservableProperty] private object? _selectedNode;

    /// <summary>Path of the selected node, kept across the wholesale rebuild in <see cref="RefreshScripts"/>
    /// (the node objects themselves don't survive it).</summary>
    private string? _selectedPath;

    partial void OnSelectedNodeChanged(object? value)
        => _selectedPath = value switch
        {
            ScriptItem item => item.FullPath,
            ScriptFolderViewModel folder => folder.FullPath,
            _ => null,
        };

    public void RefreshScripts()
    {
        Scripts.Clear();
        ScriptNodes.Clear();
        // The nodes these point at are about to be replaced.
        ClearDropTarget();
        MarkDragging(null);
        var dir = _ctx.Project?.ScriptsDirectory;
        if (dir is null || _ctx.ScriptStore.ReadTree(dir) is not { } tree) return;

        // Tabs with unsaved edits mark their backing script with a dot.
        var unsaved = Tabs.Where(t => t.IsDirty && t.ScriptPath is not null)
                           .Select(t => t.ScriptPath!)
                           .ToHashSet(StringComparer.Ordinal);
        var filter = ScriptFilter?.Trim() ?? "";
        // Either reason puts a file in the tree: its name matched, or the last content pass found the filter
        // inside it.
        bool Include(ScriptItem s) => ScriptSearch.MatchesName(s.Name, filter) || s.MatchLine is not null;
        ScriptItem Make(ScriptFileRef f) => new(f.Name, f.Path)
        {
            IsUnsaved = unsaved.Contains(f.Path),
            MatchLine = _contentHits.GetValueOrDefault(f.Path),
        };

        var scratchDir = _ctx.Project?.ScratchDirectory;
        bool IsScratchFolder(string path) => scratchDir is not null
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(scratchDir), StringComparison.OrdinalIgnoreCase);

        BuildScriptNodes(tree, ScriptNodes, filter, Include, Make, IsScratchFolder);

        // Re-point the selection at the rebuilt node for the same path (or drop it if the file is gone).
        SelectedNode = _selectedPath is { } path ? Locate(path) : null;
    }

    /// <summary>
    /// Select the node for <paramref name="absolutePath"/> and expand everything above it — "Reveal in
    /// Scripts", the answer to "which file is this tab?". Returns false when the path isn't under the
    /// project's scripts folder at all; the filter hiding it — by name or by contents — is not a failure,
    /// it's cleared first.
    /// </summary>
    public bool Reveal(string absolutePath)
    {
        if (Locate(absolutePath) is null)
        {
            // Either the tree predates the file (one created outside the app), or the filter is hiding it.
            // Clearing the filter re-reads the tree too, so both routes end in a refresh.
            if (ScriptFilter.Length > 0) ScriptFilter = ""; else RefreshScripts();
        }

        var chain = ScriptTreeReveal.PathTo(ScriptNodes, absolutePath);
        if (chain.Count == 0) return false;

        // Expand the ancestors before selecting: the scratch folder in particular is collapsed by default,
        // and a selection inside a collapsed folder is invisible.
        foreach (var node in chain)
            if (node is ScriptFolderViewModel folder) folder.IsExpanded = true;

        SelectedNode = chain[^1];
        return true;
    }

    /// <summary>The tree node for a path, or null when the tree doesn't hold it.</summary>
    private object? Locate(string absolutePath)
    {
        var chain = ScriptTreeReveal.PathTo(ScriptNodes, absolutePath);
        return chain.Count == 0 ? null : chain[^1];
    }

    /// <summary>Recursively fill <paramref name="target"/> with subfolders (each nested) then scripts;
    /// returns how many scripts (matching the filter) are under this node. Also feeds the flat
    /// <see cref="Scripts"/> list. Empty folders show when unfiltered; while filtering, a folder shows
    /// only if it has a matching descendant. The scratch folder is pinned above the curated folders
    /// (it's the app's, not the user's) and collapsed by default so it stays out of the way.</summary>
    private int BuildScriptNodes(ScriptTree node, IList<object> target,
        string filter, Func<ScriptItem, bool> include, Func<ScriptFileRef, ScriptItem> make,
        Func<string, bool> isScratchFolder)
    {
        var total = 0;
        foreach (var sub in node.Folders)
        {
            var scratch = isScratchFolder(sub.Path);
            var folder = new ScriptFolderViewModel(sub.Name, sub.Path)
            {
                IsExpanded = filter.Length > 0 || !scratch,
                IsScratch = scratch,
            };
            var n = BuildScriptNodes(sub, folder.Children, filter, include, make, isScratchFolder);
            folder.Count = n;
            total += n;
            if (n > 0 || filter.Length == 0)
            {
                if (scratch) target.Insert(0, folder); // pinned first; files are appended after all folders
                else target.Add(folder);
            }
        }
        foreach (var file in node.Files)
        {
            var item = make(file);
            Scripts.Add(item);
            if (include(item)) { target.Add(item); total++; }
        }
        return total;
    }

    /// <summary>Create a new folder under <paramref name="parentDir"/> (defaults to the scripts root).</summary>
    public void CreateScriptFolder(string name, string? parentDir = null)
    {
        var root = _ctx.Project?.ScriptsDirectory;
        if (root is null || string.IsNullOrWhiteSpace(name)) return;
        var parent = parentDir ?? root;
        var safe = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
        if (safe.Length == 0) return;
        try { _ctx.ScriptStore.CreateFolder(Path.Combine(parent, safe)); }
        catch (Exception ex) { _ctx.SetStatus($"Could not create folder: {ex.Message}"); return; }
        RefreshScripts();
        _ctx.SetStatus($"Created folder {safe}.");
    }

    /// <summary>Create an empty .sql file in <paramref name="dir"/>; returns its path (null on clash/error).</summary>
    public async Task<string?> CreateScriptFileAsync(string dir, string name)
    {
        if (!_ctx.ScriptStore.DirectoryExists(dir) || string.IsNullOrWhiteSpace(name)) return null;
        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) name += ".sql";
        var path = Path.Combine(dir, name);
        if (_ctx.ScriptStore.FileExists(path)) { _ctx.SetStatus($"{name} already exists."); return null; }
        try { await _ctx.ScriptStore.WriteTextAsync(path, "", CancellationToken.None); }
        catch (Exception ex) { _ctx.SetStatus($"Could not create script: {ex.Message}"); return null; }
        RefreshScripts();
        return path;
    }

    /// <summary>Move a script file into <paramref name="targetDir"/> (drag &amp; drop between folders).</summary>
    public void MoveScript(string sourcePath, string targetDir)
    {
        if (!_ctx.ScriptStore.FileExists(sourcePath) || !_ctx.ScriptStore.DirectoryExists(targetDir)) return;
        var dest = Path.Combine(targetDir, Path.GetFileName(sourcePath));
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.Ordinal)) return;
        if (_ctx.ScriptStore.FileExists(dest)) { _ctx.SetStatus($"{Path.GetFileName(dest)} already exists there."); return; }
        try { _ctx.ScriptStore.Move(sourcePath, dest); }
        catch (Exception ex) { _ctx.SetStatus($"Move failed: {ex.Message}"); return; }

        foreach (var t in Tabs)
            if (string.Equals(t.ScriptPath, sourcePath, StringComparison.Ordinal)) Repoint(t, dest);
        RefreshScripts();
        _ctx.SetStatus($"Moved {Path.GetFileName(sourcePath)}.");
    }

    /// <summary>
    /// Delete a script file. The caller has confirmed, and is responsible for the tabs that were backing it
    /// (see <see cref="WorkspaceViewModel.DeleteScript"/>) — deleting a file under a live tab whose
    /// autosave is still armed would simply put it back.
    /// </summary>
    /// <returns>False when the delete failed; the reason is in the status bar.</returns>
    public bool DeleteScript(string path)
    {
        try { _ctx.ScriptStore.Delete(path); }
        catch (Exception ex)
        {
            _ctx.SetStatus($"Could not delete {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
        RefreshScripts();
        _ctx.SetStatus($"Deleted {Path.GetFileName(path)}.");
        return true;
    }

    /// <summary>Rename a script, optionally relocating it to <paramref name="targetDir"/> in the same move
    /// — that combination is how naming a scratch tab promotes its file out of the scratch folder.</summary>
    public async Task RenameScriptAsync(string oldPath, string newName, string? targetDir = null)
    {
        var dir = targetDir ?? Path.GetDirectoryName(oldPath);
        if (dir is null) return;
        if (!newName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) newName += ".sql";
        var newPath = Path.Combine(dir, newName);
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return;
        if (_ctx.ScriptStore.FileExists(newPath)) { _ctx.SetStatus($"A script named {newName} already exists."); return; }

        try { await Task.Run(() => _ctx.ScriptStore.Move(oldPath, newPath)); }
        catch (Exception ex) { _ctx.SetStatus($"Rename failed: {ex.Message}"); return; }

        foreach (var t in Tabs)
            if (string.Equals(t.ScriptPath, oldPath, StringComparison.Ordinal)) Repoint(t, newPath);
        RefreshScripts();
        _updateTitle();
        _ctx.SetStatus($"Renamed to {newName}.");
    }

    /// <summary>Point an open tab at a file's new location and re-derive whether it's still scratch —
    /// dragging a file into or out of the scratch folder changes what the tab is, not just where it lives.</summary>
    private void Repoint(EditorTabViewModel tab, string newPath)
    {
        tab.ScriptPath = newPath;
        tab.IsScratch = Bearing.App.Workspace.ScratchNaming.IsUnderScratch(newPath, _ctx.Project?.ScratchDirectory);
    }
}
