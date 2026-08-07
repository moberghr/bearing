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
/// Keeps every scratch tab backed by a real file under the project's scratch folder, written as you type
/// (debounced). This is what makes unnamed work committable and greppable instead of living only inside
/// <c>session.json</c>.
///
/// <para>The file is created <b>lazily, on first non-blank content</b> — not when the tab opens. Opening a
/// tab you never type in would otherwise leave an empty file behind forever, and there is deliberately no
/// cleanup pass to sweep them up.</para>
///
/// <para>Writes are best-effort in the same spirit as the rest of the persistence layer: a failure leaves
/// the buffer untouched and surfaces in the status bar, and the tab keeps its unsaved-work status so the
/// close prompt still catches it.</para>
/// </summary>
public sealed class ScratchAutosave : IDisposable
{
    private readonly WorkspaceContext _ctx;
    private readonly TimeSpan _debounce;
    private readonly Func<DateOnly> _today;
    private readonly Dictionary<EditorTabViewModel, CancellationTokenSource> _pending = new();
    private readonly HashSet<EditorTabViewModel> _watched = new();
    private bool _disposed;

    public ScratchAutosave(WorkspaceContext ctx, TimeSpan? debounce = null, Func<DateOnly>? today = null)
    {
        _ctx = ctx;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(600);
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _ctx.Tabs.CollectionChanged += OnTabsChanged;
        foreach (var tab in _ctx.Tabs) Watch(tab);
    }

    /// <summary>Raised after a scratch file is created or updated, so the scripts tree can refresh.</summary>
    public event Action? Saved;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.NewItems?.OfType<EditorTabViewModel>() ?? Enumerable.Empty<EditorTabViewModel>())
            Watch(tab);
        foreach (var tab in e.OldItems?.OfType<EditorTabViewModel>() ?? Enumerable.Empty<EditorTabViewModel>())
            Unwatch(tab);
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
        if (sender is EditorTabViewModel { IsScratch: true } tab) Schedule(tab);
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
    /// Write one scratch tab's buffer now, bypassing the debounce — used on the paths that must not lose a
    /// pending write (tab close, project switch, shutdown). No-op for a named script or an empty buffer.
    /// </summary>
    public async Task FlushAsync(EditorTabViewModel tab)
    {
        if (_pending.Remove(tab, out var cts)) { cts.Cancel(); cts.Dispose(); }
        await SaveAsync(tab);
    }

    /// <summary>Flush every watched scratch tab (project switch).</summary>
    public async Task FlushAllAsync()
    {
        foreach (var tab in _watched.ToList()) await FlushAsync(tab);
    }

    /// <summary>
    /// Synchronous last-chance write for the shutdown path, which can't await. Deliberately narrower than
    /// <see cref="FlushAllAsync"/>: it only rewrites scratch tabs that <b>already have a file</b>, and it
    /// touches no view-model property. Creating a file here would mean assigning <c>ScriptPath</c> — a
    /// property change raised on whichever thread is tearing the app down, with bindings still live.
    /// A brand-new scratch buffer loses nothing by being skipped: its text is in <c>session.json</c>, and
    /// autosave gives it a file on the next keystroke after restart.
    /// </summary>
    public void FlushExistingBlocking()
    {
        foreach (var tab in _watched.ToList())
        {
            if (!tab.IsScratch || tab.ScriptPath is not { } path || tab.Text.Trim().Length == 0) continue;
            var text = tab.Text;
            try { Task.Run(() => _ctx.ScriptStore.WriteTextAsync(path, text, CancellationToken.None)).GetAwaiter().GetResult(); }
            catch { /* best-effort on shutdown (§5.2) */ }
        }
    }

    private async Task SaveAsync(EditorTabViewModel tab)
    {
        if (!tab.IsScratch || _ctx.Project is not { } project) return;
        if (tab.Text.Trim().Length == 0) return;   // nothing worth a file yet

        try
        {
            var path = tab.ScriptPath ?? CreatePath(project);
            await _ctx.ScriptStore.WriteTextAsync(path, tab.Text, CancellationToken.None);
            var isNew = tab.ScriptPath is null;
            tab.ScriptPath = path;
            tab.MarkSaved(tab.Text);
            if (isNew) Saved?.Invoke();            // a new file changes the tree; an update doesn't
        }
        catch (Exception ex)
        {
            // Best-effort (§5.2): never take the buffer down with the write. HasUnsavedWork still reports
            // true while ScriptPath is null, so the close prompt remains the backstop.
            _ctx.SetStatus($"Could not autosave scratch: {ex.Message}");
        }
    }

    /// <summary>Reserve the next free dated filename in the scratch folder.</summary>
    private string CreatePath(Project project)
    {
        var dir = project.ScratchDirectory;
        _ctx.ScriptStore.CreateFolder(dir);
        var existing = _ctx.ScriptStore.ReadTree(dir)?.Files.Select(f => f.Name) ?? Enumerable.Empty<string>();
        // Names already claimed by other open tabs count as taken — two tabs typing on the same day must
        // not race onto the same file before either has been written.
        var claimed = _ctx.Tabs.Where(t => t.ScriptPath is not null)
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
