using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;

namespace Bearing.App.Workspace;

/// <summary>
/// Writes editor buffers to disk without an explicit Save. Two jobs that share one debounce:
///
/// <list type="bullet">
/// <item><b>Scratch buffers</b> are kept backed by a real file under the project's scratch folder — that
/// file is the buffer's only home, so it is written at the checkpoints that would otherwise lose it (tab
/// close, project switch, shutdown) in <i>every</i> mode, <see cref="AutosaveMode.Off"/> included.</item>
/// <item><b>Named scripts</b> are the user's, so they are written only when
/// <see cref="AutosaveMode"/> says so. Under <see cref="AutosaveMode.Off"/> they go dirty and the
/// close prompt is what guards them.</item>
/// </list>
///
/// <para>A scratch file is created <b>lazily, on first non-blank content</b> — not when the tab opens.
/// Opening a tab you never type in would otherwise leave an empty file behind forever, and there is
/// deliberately no cleanup pass to sweep them up.</para>
///
/// <para>Writes are best-effort in the spirit of the rest of the persistence layer (§5.2): a failure leaves
/// the buffer untouched and surfaces in the status bar, and the tab keeps its unsaved-work status so the
/// close prompt still catches it.</para>
/// </summary>
public sealed class TabAutosave : IDisposable
{
    private readonly WorkspaceContext _ctx;
    private readonly TimeSpan _debounce;
    private readonly Func<DateOnly> _today;
    private readonly Dictionary<EditorTabViewModel, CancellationTokenSource> _pending = new();
    private readonly HashSet<EditorTabViewModel> _watched = new();
    private bool _disposed;

    public TabAutosave(WorkspaceContext ctx, TimeSpan? debounce = null, Func<DateOnly>? today = null)
    {
        _ctx = ctx;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(600);
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _ctx.Tabs.CollectionChanged += OnTabsChanged;
        foreach (var tab in _ctx.Tabs) Watch(tab);
    }

    private AutosaveMode Mode => _ctx.Settings.AutosaveMode;

    /// <summary>Raised after a scratch file is created, so the scripts tree can pick it up.</summary>
    public event Action? FileCreated;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.NewItems?.OfType<EditorTabViewModel>() ?? Enumerable.Empty<EditorTabViewModel>())
            Watch(tab);
        foreach (var tab in e.OldItems?.OfType<EditorTabViewModel>() ?? Enumerable.Empty<EditorTabViewModel>())
        {
            // Leaving the visible tab list is not the same as being closed: a project switch parks tabs,
            // and a parked tab is still live (it may even have a query running). Only stop watching a tab
            // that has left every open project.
            if (!_ctx.AllTabs.Contains(tab)) Unwatch(tab);
        }
    }

    private void Watch(EditorTabViewModel tab)
    {
        if (_watched.Add(tab)) tab.PropertyChanged += OnTabPropertyChanged;
    }

    private void Unwatch(EditorTabViewModel tab)
    {
        if (!_watched.Remove(tab)) return;
        tab.PropertyChanged -= OnTabPropertyChanged;
        if (_pending.Remove(tab, out var cts)) { cts.Cancel(); cts.Dispose(); }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorTabViewModel.Text)) return;
        if (Mode != AutosaveMode.OnEdit) return;   // other modes don't write while typing
        if (sender is EditorTabViewModel tab) Schedule(tab);
    }

    private void Schedule(EditorTabViewModel tab)
    {
        if (_disposed) return;
        if (_pending.Remove(tab, out var prior)) { prior.Cancel(); prior.Dispose(); }

        var cts = new CancellationTokenSource();
        _pending[tab] = cts;
        _ = DebouncedSaveAsync(tab, cts.Token);
    }

    private async Task DebouncedSaveAsync(EditorTabViewModel tab, CancellationToken ct)
    {
        try
        {
            if (_debounce > TimeSpan.Zero) await Task.Delay(_debounce, ct);
            if (ct.IsCancellationRequested) return;
            await SaveAsync(tab);
        }
        catch (OperationCanceledException) { /* superseded by a later keystroke */ }
    }

    /// <summary>
    /// Write one tab's buffer now, bypassing the debounce — the checkpoints that must not lose a pending
    /// write (tab close, project switch). Scratch is written whatever the mode; a named script only when
    /// the mode already writes it, so a checkpoint can't sneak past <see cref="AutosaveMode.Off"/>.
    /// </summary>
    public async Task FlushAsync(EditorTabViewModel tab)
    {
        if (_pending.Remove(tab, out var cts)) { cts.Cancel(); cts.Dispose(); }
        if (!tab.IsScratch && Mode == AutosaveMode.Off) return;
        await SaveAsync(tab);
    }

    /// <summary>Flush every watched tab (project switch).</summary>
    public async Task FlushAllAsync()
    {
        foreach (var tab in _watched.ToList()) await FlushAsync(tab);
    }

    /// <summary>The tab just ran its SQL — the write point for <see cref="AutosaveMode.OnExecute"/>.</summary>
    public Task OnExecutedAsync(EditorTabViewModel tab)
        => Mode == AutosaveMode.OnExecute ? SaveAsync(tab) : Task.CompletedTask;

    /// <summary>
    /// Synchronous last-chance write for the shutdown path, which can't await. Deliberately narrower than
    /// <see cref="FlushAllAsync"/>: it only rewrites tabs that <b>already have a file</b>, and it touches no
    /// view-model property. Creating a file here would mean assigning <c>ScriptPath</c> — a property change
    /// raised on whichever thread is tearing the app down, with bindings still live. A brand-new scratch
    /// buffer loses nothing by being skipped: its text is in <c>session.json</c>, and it gets a file on the
    /// next keystroke after restart.
    /// </summary>
    public void FlushExistingBlocking()
    {
        foreach (var tab in _watched.ToList())
        {
            if (!tab.IsScratch && Mode == AutosaveMode.Off) continue;
            if (tab.ScriptPath is not { } path || tab.Text.Trim().Length == 0) continue;
            var text = tab.Text;
            try { Task.Run(() => _ctx.ScriptStore.WriteTextAsync(path, text, CancellationToken.None)).GetAwaiter().GetResult(); }
            catch { /* best-effort on shutdown (§5.2) */ }
        }
    }

    private async Task SaveAsync(EditorTabViewModel tab)
    {
        if (tab.Text.Trim().Length == 0 && tab.ScriptPath is null) return;   // nothing worth a file yet
        if (!tab.IsScratch && tab.ScriptPath is null) return;                // a named tab always has a path

        // A scratch file belongs in the scratch folder of the tab's *own* project — a parked tab still
        // autosaves while another project is on screen, and its text must not land in that project's folder.
        var owner = _ctx.ProjectOf(tab) ?? _ctx.Project;
        if (tab.IsScratch && owner is null) return;                          // nowhere to put a scratch file

        try
        {
            var path = tab.ScriptPath ?? CreateScratchPath(owner!);
            await _ctx.ScriptStore.WriteTextAsync(path, tab.Text, CancellationToken.None);
            var isNew = tab.ScriptPath is null;
            tab.ScriptPath = path;
            tab.MarkSaved(tab.Text);
            if (isNew) FileCreated?.Invoke();      // a new file changes the tree; an update doesn't
        }
        catch (Exception ex)
        {
            // Best-effort (§5.2): never take the buffer down with the write. For scratch, HasUnsavedWork
            // still reports true while ScriptPath is null; for a named script IsDirty stays true. Either
            // way the close prompt remains the backstop.
            _ctx.SetStatus($"Could not autosave {tab.Header}: {ex.Message}");
        }
    }

    /// <summary>Reserve the next free dated filename in the scratch folder.</summary>
    private string CreateScratchPath(Project project)
    {
        var dir = project.ScratchDirectory;
        _ctx.ScriptStore.CreateFolder(dir);
        var existing = _ctx.ScriptStore.ReadTree(dir)?.Files.Select(f => f.Name) ?? Enumerable.Empty<string>();
        // Names already claimed by other open tabs count as taken — two tabs typing on the same day must
        // not race onto the same file before either has been written. Scanned across every open project's
        // tabs (parked included), since they can share this scratch folder.
        var claimed = _ctx.AllTabs.Where(t => t.ScriptPath is not null)
            .Select(t => Path.GetFileName(t.ScriptPath!));
        return Path.Combine(dir, ScratchNaming.NextFileName(_today(), existing.Concat(claimed)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx.Tabs.CollectionChanged -= OnTabsChanged;
        foreach (var tab in _watched.ToList()) Unwatch(tab);
    }
}
