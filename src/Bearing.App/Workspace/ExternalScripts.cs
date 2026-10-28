using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.Services;
using Bearing.App.ViewModels;

namespace Bearing.App.Workspace;

/// <summary>
/// Scripts that come from outside a project's <c>scripts</c> folder — <c>bearing file.sql</c> — and the two
/// ways of settling where one belongs: copy it into the project, or move its tab to another project.
/// <para>
/// <b>An opened file stays where it is.</b> The tab edits and saves the file at its own path and runs it
/// against the project it was opened in — the tab's <see cref="EditorTabViewModel.ProjectDirectory"/> is what
/// its connection is looked up in. Nothing is copied until the user asks, because a file that lives in some
/// other repository is usually the copy that matters.
/// </para>
/// <para>
/// A coordinator rather than more shell (§9.1): it needs the workspace, the scripts tree, the connection
/// display and the project switch, and the shell keeps one delegating member per operation.
/// </para>
/// </summary>
public sealed class ExternalScripts
{
    private readonly WorkspaceContext _ctx;
    private readonly WorkspaceViewModel _workspace;
    private readonly ScriptsViewModel _scripts;
    private readonly ConnectionsViewModel _connections;
    private readonly Func<string, Task> _openProject;

    public ExternalScripts(WorkspaceContext ctx, WorkspaceViewModel workspace, ScriptsViewModel scripts,
        ConnectionsViewModel connections, Func<string, Task> openProject)
    {
        _ctx = ctx;
        _workspace = workspace;
        _scripts = scripts;
        _connections = connections;
        _openProject = openProject;
    }

    /// <summary>
    /// Open each file in a tab of the project on screen. A file some tab already shows — in this project or
    /// a parked one — is brought forward instead of opened twice: two tabs on one file would each save over
    /// the other's edits.
    /// </summary>
    public async Task OpenAsync(IReadOnlyList<string> paths)
    {
        foreach (var raw in paths)
        {
            string path;
            try { path = Path.GetFullPath(raw); }
            catch { _ctx.SetStatus($"Could not open '{raw}': not a usable path."); continue; }

            if (!_ctx.ScriptStore.FileExists(path)) { _ctx.SetStatus($"Could not open {path}: no such file."); continue; }

            if (_workspace.AllTabs.FirstOrDefault(t => SamePath(t.ScriptPath, path)) is { } open)
            {
                if (open.ProjectDirectory is { } home && !SamePath(home, _ctx.Project?.Directory))
                    await _openProject(home);
                _workspace.SelectedTab = open;
                _ctx.SetStatus($"{Path.GetFileName(path)} is already open.");
                continue;
            }

            try { await _workspace.OpenScriptInNewTabAsync(path); }
            catch (Exception ex) { _ctx.SetStatus($"Could not open {Path.GetFileName(path)}: {ex.Message}"); continue; }

            if (_ctx.Project is { } project && !IsInProjectScripts(path, project.ScriptsDirectory))
                _ctx.SetStatus(
                    $"Opened {Path.GetFileName(path)} from {Path.GetDirectoryName(path)} — it runs against "
                    + $"'{project.Manifest.Name}'. Its tab menu copies it into this project or moves it to another.");
        }
    }

    /// <summary>True when the tab's file is somewhere other than its own project's scripts folder — the tabs
    /// that "Copy into project scripts" is for.</summary>
    public bool IsOutsideProject(EditorTabViewModel tab)
        => tab.ScriptPath is { } path
            && _ctx.ProjectOf(tab) is { } project
            && !IsInProjectScripts(path, project.ScriptsDirectory);

    /// <summary>
    /// Save the tab's buffer as a new script in its project's scripts folder and point the tab at that copy —
    /// "Save As" into the project. The original file is left exactly as it is on disk, unsaved edits included:
    /// they go to the copy, which is the file the tab edits from now on.
    /// </summary>
    /// <returns>The copy's path, or null when nothing was written.</returns>
    public async Task<string?> CopyIntoProjectAsync(EditorTabViewModel tab)
    {
        if (tab.ScriptPath is not { } source || _ctx.ProjectOf(tab) is not { } project) return null;
        if (!IsOutsideProject(tab))
        {
            _ctx.SetStatus($"{Path.GetFileName(source)} is already one of '{project.Manifest.Name}''s scripts.");
            return null;
        }

        var target = Path.Combine(project.ScriptsDirectory,
            FreeName(Path.GetFileName(source), name => _ctx.ScriptStore.FileExists(Path.Combine(project.ScriptsDirectory, name))));
        try
        {
            await _workspace.SaveScriptAsync(tab, target, tab.Text);
        }
        catch (Exception ex)
        {
            _ctx.SetStatus($"Could not copy {Path.GetFileName(source)} into the project: {ex.Message}");
            return null;
        }

        _ctx.SetStatus($"Copied to scripts/{Path.GetFileName(target)} in '{project.Manifest.Name}'; "
                       + $"{source} is unchanged. This tab now edits the copy.");
        return target;
    }

    /// <summary>
    /// Hand the tab to another project: it leaves this project's strip, that project comes on screen, and the
    /// tab runs against that project's default connection. The file does not move.
    /// <para>
    /// Refused while anything on the tab is bound to the connection it is leaving — a running statement, an
    /// open transaction (§1.10's guard table: switching a tab's connection refuses), or grid edits not yet
    /// saved, which would otherwise be written through whichever connection the tab has by then. Its results
    /// are cleared for the same reason: their paging, FK navigation and edits all go through the tab's
    /// connection, and that is about to be a different server.
    /// </para>
    /// </summary>
    /// <returns>Whether the tab moved.</returns>
    public async Task<bool> MoveToProjectAsync(EditorTabViewModel tab, string projectDirectory)
    {
        if (tab.ScriptPath is null || tab.IsScratch)
        {
            _ctx.SetStatus("Only a tab with a named file can move to another project — rename it first.");
            return false;
        }
        if (SamePath(tab.ProjectDirectory, projectDirectory)) return false;
        if (tab.IsRunning)
        {
            _ctx.SetStatus("A query is still running on this tab — cancel it or let it finish before moving it.");
            return false;
        }
        if (TransactionGuard.ReasonToRefuse(_ctx.Transactions, tab, "move it to another project") is { } blocked)
        {
            _ctx.SetStatus(blocked);
            return false;
        }
        if (tab.Results.Any(r => r.HasPendingChanges))
        {
            _ctx.SetStatus("This tab has unsaved grid edits — save or discard them before moving it.");
            return false;
        }

        // Land a pending autosave while the tab still belongs to the project the write was scheduled for.
        if (_ctx.Autosave is { } autosave) await autosave.FlushAsync(tab);

        // Off the strip first, so the outgoing project parks without it and its session.json forgets it.
        _workspace.Detach(tab);
        try
        {
            await _openProject(projectDirectory);
        }
        catch (Exception ex)
        {
            // Nowhere to go: put it back where it was rather than lose it.
            _workspace.Adopt(tab);
            _ctx.SetStatus($"Could not open that project: {ex.Message}");
            return false;
        }

        if (_ctx.Project is not { } target || !SamePath(target.Directory, projectDirectory))
        {
            _workspace.Adopt(tab);
            return false;
        }

        // The target already shows this file: keep its tab, not a second one that would save over it — but
        // carry over what was typed here, which the target's tab (same file, same disk baseline) never saw.
        if (_workspace.Tabs.FirstOrDefault(t => SamePath(t.ScriptPath, tab.ScriptPath)) is { } existing)
        {
            if (tab.IsModified) existing.Text = tab.Text;
            _workspace.SelectedTab = existing;
            _ctx.SetStatus($"{Path.GetFileName(tab.ScriptPath)} was already open in '{target.Manifest.Name}'.");
            return true;
        }

        tab.ProjectDirectory = target.Directory;
        tab.ConnectionId = _ctx.DefaultConnectionId;
        tab.DatabaseName = null;
        tab.SetFreshResults(Array.Empty<ResultSetViewModel>());
        _connections.ApplyConnectionDisplay(tab);
        _workspace.Adopt(tab);
        _scripts.RefreshScripts();

        _ctx.SetStatus($"Moved {Path.GetFileName(tab.ScriptPath)} to '{target.Manifest.Name}'"
                       + (_ctx.DefaultConnectionId is { } id && _ctx.FindConnection(id) is { } c
                           ? $" — it runs against {c.Name} now."
                           : " — pick a connection to run it."));
        return true;
    }

    /// <summary>Pure: <paramref name="fileName"/>, or <c>name (2).sql</c>, <c>name (3).sql</c>… — the first
    /// that <paramref name="taken"/> says is free. A copy never overwrites a script already in the project.</summary>
    public static string FreeName(string fileName, Func<string, bool> taken)
    {
        if (!taken(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>Pure: whether <paramref name="path"/> sits in <paramref name="scriptsDirectory"/>, at any
    /// depth. Scratch counts — it is a subfolder of scripts.</summary>
    public static bool IsInProjectScripts(string path, string scriptsDirectory)
        => ScratchNaming.IsUnderScratch(path, scriptsDirectory);

    private static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal); }
        catch { return string.Equals(a, b, StringComparison.Ordinal); }
    }
}
