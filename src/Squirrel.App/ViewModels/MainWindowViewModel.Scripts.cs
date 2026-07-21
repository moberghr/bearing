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
    // ---- Scripts -----------------------------------------------------------------------------

    /// <summary>The Scripts tree: folders (one level of subdirectories) then ungrouped root scripts.</summary>
    public ObservableCollection<object> ScriptNodes { get; } = new();

    /// <summary>Name filter for the Scripts tree (empty = show all).</summary>
    [ObservableProperty] private string _scriptFilter = "";

    partial void OnScriptFilterChanged(string value) => RefreshScripts();

    private void RefreshScripts()
    {
        Scripts.Clear();
        ScriptNodes.Clear();
        var dir = _project?.ScriptsDirectory;
        if (dir is null || !Directory.Exists(dir)) return;

        // Tabs with unsaved edits mark their backing script with a dot.
        var unsaved = Tabs.Where(t => t.IsDirty && t.ScriptPath is not null)
                          .Select(t => t.ScriptPath!)
                          .ToHashSet(StringComparer.Ordinal);
        var filter = ScriptFilter?.Trim() ?? "";
        bool Matches(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        ScriptItem Make(string path) => new(Path.GetFileName(path), path) { IsUnsaved = unsaved.Contains(path) };

        BuildScriptNodes(dir, ScriptNodes, filter, Matches, Make);
    }

    /// <summary>Recursively fill <paramref name="target"/> with subfolders (each nested) then scripts;
    /// returns how many scripts (matching the filter) are under this directory. Also feeds the flat
    /// <see cref="Scripts"/> list. Empty folders show when unfiltered; while filtering, a folder shows
    /// only if it has a matching descendant.</summary>
    private int BuildScriptNodes(string dir, System.Collections.Generic.IList<object> target,
        string filter, Func<string, bool> matches, Func<string, ScriptItem> make)
    {
        var total = 0;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var folder = new ScriptFolderViewModel(Path.GetFileName(sub), sub) { IsExpanded = filter.Length > 0 };
            var n = BuildScriptNodes(sub, folder.Children, filter, matches, make);
            folder.Count = n;
            total += n;
            if (n > 0 || filter.Length == 0) target.Add(folder);
        }
        foreach (var path in Directory.EnumerateFiles(dir, "*.sql").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var item = make(path);
            Scripts.Add(item);
            if (matches(item.Name)) { target.Add(item); total++; }
        }
        return total;
    }

    /// <summary>Create a new folder under <paramref name="parentDir"/> (defaults to the scripts root).</summary>
    public void CreateScriptFolder(string name, string? parentDir = null)
    {
        var root = _project?.ScriptsDirectory;
        if (root is null || string.IsNullOrWhiteSpace(name)) return;
        var parent = parentDir ?? root;
        var safe = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
        if (safe.Length == 0) return;
        try { Directory.CreateDirectory(Path.Combine(parent, safe)); }
        catch (Exception ex) { StatusText = $"Could not create folder: {ex.Message}"; return; }
        RefreshScripts();
        StatusText = $"Created folder {safe}.";
    }

    /// <summary>Create an empty .sql file in <paramref name="dir"/>; returns its path (null on clash/error).</summary>
    public async Task<string?> CreateScriptFileAsync(string dir, string name)
    {
        if (!Directory.Exists(dir) || string.IsNullOrWhiteSpace(name)) return null;
        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) name += ".sql";
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) { StatusText = $"{name} already exists."; return null; }
        try { await File.WriteAllTextAsync(path, "", CancellationToken.None); }
        catch (Exception ex) { StatusText = $"Could not create script: {ex.Message}"; return null; }
        RefreshScripts();
        return path;
    }

    /// <summary>Move a script file into <paramref name="targetDir"/> (drag & drop between folders).</summary>
    public void MoveScript(string sourcePath, string targetDir)
    {
        if (!File.Exists(sourcePath) || !Directory.Exists(targetDir)) return;
        var dest = Path.Combine(targetDir, Path.GetFileName(sourcePath));
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.Ordinal)) return;
        if (File.Exists(dest)) { StatusText = $"{Path.GetFileName(dest)} already exists there."; return; }
        try { File.Move(sourcePath, dest); }
        catch (Exception ex) { StatusText = $"Move failed: {ex.Message}"; return; }

        foreach (var t in Tabs)
            if (string.Equals(t.ScriptPath, sourcePath, StringComparison.Ordinal)) t.ScriptPath = dest;
        RefreshScripts();
        StatusText = $"Moved {Path.GetFileName(sourcePath)}.";
    }

    /// <summary>Open a saved script: focus its existing tab, or load it into a new one.</summary>
    public async Task OpenScriptInNewTabAsync(string absolutePath)
    {
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.ScriptPath, absolutePath, StringComparison.Ordinal));
        if (existing is not null) { SelectedTab = existing; return; }
        var text = await File.ReadAllTextAsync(absolutePath, CancellationToken.None);
        NewTab(text, absolutePath);
        StatusText = $"Opened {Path.GetFileName(absolutePath)}.";
    }

    public async Task LoadScriptIntoSelectedAsync(string absolutePath)
    {
        var text = await File.ReadAllTextAsync(absolutePath, CancellationToken.None);
        var tab = SelectedTab ?? NewTab();
        tab.Text = text;
        tab.ScriptPath = absolutePath;
        tab.MarkSaved(text);
        RefreshScripts();
        UpdateTitle();
        StatusText = $"Opened {Path.GetFileName(absolutePath)}.";
    }

    public async Task SaveSelectedScriptAsync(string absolutePath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, text, CancellationToken.None);
        var tab = SelectedTab ?? NewTab(text);
        tab.Text = text;
        tab.ScriptPath = absolutePath;
        tab.MarkSaved(text);
        RefreshScripts();
        UpdateTitle();
        StatusText = $"Saved {Path.GetFileName(absolutePath)}.";
    }

    // ---- Rename ------------------------------------------------------------------------------

    /// <summary>Rename the selected tab: a scratch label, or the backing .sql file on disk.</summary>
    public async Task RenameTabAsync(EditorTabViewModel tab, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (tab.IsScratch) tab.DisplayName = newName.Trim();
        else if (tab.ScriptPath is { } path) await RenameScriptAsync(path, newName.Trim());
    }

    public async Task RenameScriptAsync(string oldPath, string newName)
    {
        var dir = Path.GetDirectoryName(oldPath);
        if (dir is null) return;
        if (!newName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) newName += ".sql";
        var newPath = Path.Combine(dir, newName);
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return;
        if (File.Exists(newPath)) { StatusText = $"A script named {newName} already exists."; return; }

        try { await Task.Run(() => File.Move(oldPath, newPath)); }
        catch (Exception ex) { StatusText = $"Rename failed: {ex.Message}"; return; }

        foreach (var t in Tabs)
            if (string.Equals(t.ScriptPath, oldPath, StringComparison.Ordinal)) t.ScriptPath = newPath;
        RefreshScripts();
        UpdateTitle();
        StatusText = $"Renamed to {newName}.";
    }
}
