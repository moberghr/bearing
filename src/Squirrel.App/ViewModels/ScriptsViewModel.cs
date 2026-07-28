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

    /// <summary>Name filter for the Scripts tree (empty = show all).</summary>
    [ObservableProperty] private string _scriptFilter = "";

    partial void OnScriptFilterChanged(string value) => RefreshScripts();

    public void RefreshScripts()
    {
        Scripts.Clear();
        ScriptNodes.Clear();
        var dir = _ctx.Project?.ScriptsDirectory;
        if (dir is null || _ctx.ScriptStore.ReadTree(dir) is not { } tree) return;

        // Tabs with unsaved edits mark their backing script with a dot.
        var unsaved = Tabs.Where(t => t.IsDirty && t.ScriptPath is not null)
                           .Select(t => t.ScriptPath!)
                           .ToHashSet(StringComparer.Ordinal);
        var filter = ScriptFilter?.Trim() ?? "";
        bool Matches(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        ScriptItem Make(ScriptFileRef f) => new(f.Name, f.Path) { IsUnsaved = unsaved.Contains(f.Path) };

        BuildScriptNodes(tree, ScriptNodes, filter, Matches, Make);
    }

    /// <summary>Recursively fill <paramref name="target"/> with subfolders (each nested) then scripts;
    /// returns how many scripts (matching the filter) are under this node. Also feeds the flat
    /// <see cref="Scripts"/> list. Empty folders show when unfiltered; while filtering, a folder shows
    /// only if it has a matching descendant.</summary>
    private int BuildScriptNodes(ScriptTree node, IList<object> target,
        string filter, Func<string, bool> matches, Func<ScriptFileRef, ScriptItem> make)
    {
        var total = 0;
        foreach (var sub in node.Folders)
        {
            var folder = new ScriptFolderViewModel(sub.Name, sub.Path) { IsExpanded = filter.Length > 0 };
            var n = BuildScriptNodes(sub, folder.Children, filter, matches, make);
            folder.Count = n;
            total += n;
            if (n > 0 || filter.Length == 0) target.Add(folder);
        }
        foreach (var file in node.Files)
        {
            var item = make(file);
            Scripts.Add(item);
            if (matches(item.Name)) { target.Add(item); total++; }
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
            if (string.Equals(t.ScriptPath, sourcePath, StringComparison.Ordinal)) t.ScriptPath = dest;
        RefreshScripts();
        _ctx.SetStatus($"Moved {Path.GetFileName(sourcePath)}.");
    }

    public async Task RenameScriptAsync(string oldPath, string newName)
    {
        var dir = Path.GetDirectoryName(oldPath);
        if (dir is null) return;
        if (!newName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) newName += ".sql";
        var newPath = Path.Combine(dir, newName);
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return;
        if (_ctx.ScriptStore.FileExists(newPath)) { _ctx.SetStatus($"A script named {newName} already exists."); return; }

        try { await Task.Run(() => _ctx.ScriptStore.Move(oldPath, newPath)); }
        catch (Exception ex) { _ctx.SetStatus($"Rename failed: {ex.Message}"); return; }

        foreach (var t in Tabs)
            if (string.Equals(t.ScriptPath, oldPath, StringComparison.Ordinal)) t.ScriptPath = newPath;
        RefreshScripts();
        _updateTitle();
        _ctx.SetStatus($"Renamed to {newName}.");
    }
}
