using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Bearing.App.Services;
using Bearing.App.ViewModels;

namespace Bearing.App.Workspace;

/// <summary>
/// Keeps open tabs honest about the files behind them when something other than Bearing changes those files:
/// another editor, <c>git checkout</c>, a sync client.
/// <para>
/// <b>Before</b>, a tab knew only what it last read or wrote, and every write was blind: autosave, Save and
/// the shutdown flush put the buffer over whatever the file held by then. <b>Now</b> a tab's
/// <see cref="EditorTabViewModel.SavedText"/> is compared with the disk, and the result decides:
/// </para>
/// <list type="bullet">
/// <item>disk still says <c>SavedText</c> — nothing happened;</item>
/// <item>disk already says what the tab says — just adopt it as the baseline;</item>
/// <item>the tab has no edits of its own — <b>reload</b> it, quietly;</item>
/// <item>both sides changed — a <b>conflict</b>: the tab is flagged
///   (<see cref="EditorTabViewModel.HasDiskConflict"/>), nothing writes the file, and the user is asked
///   which copy wins.</item>
/// </list>
/// <para>
/// Content, not timestamps: a write of our own lands as exactly the text we then mark saved, so it can never
/// read as somebody else's change, and a <c>touch</c> that changes nothing is nothing. The one gap — a file
/// caught half-written by us — is closed by <see cref="ScriptWriteTracker"/>.
/// </para>
/// <para>
/// When it looks: a <see cref="FileSystemWatcher"/> on each directory holding an open tab's file
/// (debounced), the window coming to the front (watchers miss things — network drives, inotify limits — and
/// that is when someone who edited elsewhere comes back), and before every write.
/// </para>
/// </summary>
public sealed class ScriptFileSync : IDisposable
{
    /// <summary>How the disk compares with a tab — see <see cref="Classify"/>.</summary>
    public enum DiskState
    {
        /// <summary>The file holds what the tab last read or wrote.</summary>
        Unchanged,
        /// <summary>No file at that path any more.</summary>
        Missing,
        /// <summary>Changed, but to exactly the tab's text — nothing to resolve.</summary>
        MatchesBuffer,
        /// <summary>Changed, and the tab has no edits of its own: reload it.</summary>
        ChangedClean,
        /// <summary>Changed, and the tab has edits of its own: somebody's work loses unless asked.</summary>
        Conflict,
    }

    private readonly WorkspaceContext _ctx;
    private readonly IDialogService? _dialogs;
    private readonly Action<Action> _post;
    private readonly TimeSpan _debounce;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly HashSet<EditorTabViewModel> _asking = new();
    private readonly object _gate = new();
    private HashSet<string> _paths = new(StringComparer.Ordinal);
    private Timer? _timer;
    private bool _checking, _again, _disposed;

    public ScriptFileSync(WorkspaceContext ctx, IDialogService? dialogs,
        Action<Action>? post = null, TimeSpan? debounce = null)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        _ctx.Tabs.CollectionChanged += OnTabsChanged;
    }

    // ---- pure -----------------------------------------------------------------------------------

    /// <summary>Pure: how <paramref name="disk"/> (null = no file) stands against a tab whose last-known file
    /// content is <paramref name="baseline"/> and whose buffer is <paramref name="text"/>.</summary>
    public static DiskState Classify(string? disk, string baseline, string text)
    {
        if (disk is null) return DiskState.Missing;
        if (string.Equals(disk, baseline, StringComparison.Ordinal)) return DiskState.Unchanged;
        if (string.Equals(disk, text, StringComparison.Ordinal)) return DiskState.MatchesBuffer;
        return string.Equals(text, baseline, StringComparison.Ordinal) ? DiskState.ChangedClean : DiskState.Conflict;
    }

    /// <summary>Pure: the fingerprint <c>session.json</c> keeps of a tab's baseline (SHA-256, hex).</summary>
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---- looking ----------------------------------------------------------------------------------

    /// <summary>Compare every open file-backed tab, parked ones included, with its file and act on it. Called
    /// on the UI thread. A call that arrives while one is running makes that one go round again.</summary>
    public async Task CheckAllAsync()
    {
        if (_disposed) return;
        if (_checking) { _again = true; return; }
        _checking = true;
        try
        {
            do
            {
                _again = false;
                SyncWatchers();
                foreach (var tab in _ctx.AllTabs.Where(t => t.ScriptPath is not null).ToList())
                    await CheckAsync(tab);
            } while (_again && !_disposed);
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task CheckAsync(EditorTabViewModel tab)
    {
        if (tab.ScriptPath is not { } path) return;
        if (_ctx.ScriptWrites.IsWriting(path)) { Schedule(); return; }

        string? disk;
        try { disk = await ReadAsync(path); }
        catch { return; }   // locked or unreadable for a moment: no answer is not a change
        if (tab.ScriptPath != path) return;                       // repointed while we read
        if (_ctx.ScriptWrites.IsWriting(path)) { Schedule(); return; }

        var state = Classify(disk, tab.SavedText, tab.Text);
        if (state == DiskState.Missing)
        {
            if (_missing.Add(path))
                _ctx.SetStatus($"{Path.GetFileName(path)} is no longer on disk — saving its tab will write it again.");
            return;
        }
        _missing.Remove(path);

        switch (state)
        {
            case DiskState.Unchanged:
                // Back to what the tab was based on (an undo elsewhere): there is nothing left to choose.
                tab.HasDiskConflict = false;
                break;
            case DiskState.MatchesBuffer:
                tab.MarkSaved(disk!);
                tab.HasDiskConflict = false;
                break;
            case DiskState.ChangedClean:
                Reload(tab, disk!);
                _ctx.SetStatus($"Reloaded {Path.GetFileName(path)} — it changed on disk.");
                break;
            case DiskState.Conflict when !tab.HasDiskConflict:
                // Once per conflict. "Decide later" must stay decided until something changes again, or
                // every focus of the window would ask the same question.
                tab.HasDiskConflict = true;
                await AskAsync(tab, writeOnOverwrite: true);
                break;
        }
    }

    // ---- standing between a write and the file ------------------------------------------------------

    /// <summary>
    /// Autosave's question: may this tab's buffer go to its file now? Never asks — autosave runs on a timer,
    /// at project switches and at close, none of them a moment to put a dialog up. A conflict found here is
    /// flagged, reported, and asked about separately.
    /// </summary>
    public async Task<bool> MayAutosaveAsync(EditorTabViewModel tab)
    {
        if (tab.ScriptPath is not { } path) return true;     // a first write: there is no file to lose
        if (tab.HasDiskConflict) return false;

        string? disk;
        try { disk = await ReadAsync(path); }
        catch { return true; }   // can't read it: let the write meet the same problem and report it

        switch (Classify(disk, tab.SavedText, tab.Text))
        {
            case DiskState.Missing:
                return true;
            case DiskState.Unchanged:
                return !string.Equals(tab.Text, tab.SavedText, StringComparison.Ordinal);   // else nothing to write
            case DiskState.MatchesBuffer:
                tab.MarkSaved(disk!);
                return false;
            case DiskState.ChangedClean:
                Reload(tab, disk!);
                _ctx.SetStatus($"Reloaded {Path.GetFileName(path)} — it changed on disk.");
                return false;
            default:
                tab.HasDiskConflict = true;
                _ctx.SetStatus($"{Path.GetFileName(path)} changed on disk — autosave stopped for it until you choose.");
                _post(() => CrashReporter.Observe(AskAsync(tab, writeOnOverwrite: true), "script disk conflict"));
                return false;
        }
    }

    /// <summary>
    /// The shutdown flush's question, answered without awaiting the UI and without touching the tab: only
    /// a file that still holds the tab's baseline (or is gone) may be written.
    /// </summary>
    public bool MayWriteOnShutdown(EditorTabViewModel tab)
    {
        if (tab.ScriptPath is not { } path) return true;
        if (tab.HasDiskConflict) return false;
        try
        {
            var disk = Task.Run(() => ReadAsync(path)).GetAwaiter().GetResult();
            return Classify(disk, tab.SavedText, tab.Text) is DiskState.Unchanged or DiskState.Missing;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// An explicit Save's question, for a tab saving to its own file. Asks when both sides changed; a file
    /// changed under a tab with no edits is reloaded rather than reverted. True means write.
    /// </summary>
    public async Task<bool> MaySaveAsync(EditorTabViewModel tab)
    {
        if (tab.ScriptPath is not { } path) return true;

        string? disk;
        try { disk = await ReadAsync(path); }
        catch { return true; }

        var state = Classify(disk, tab.SavedText, tab.Text);
        if (state == DiskState.ChangedClean)
        {
            Reload(tab, disk!);
            _ctx.SetStatus($"{Path.GetFileName(path)} had changed on disk; reloaded it — there was nothing to save.");
            return false;
        }
        if (state == DiskState.Conflict || (tab.HasDiskConflict && state != DiskState.MatchesBuffer))
        {
            tab.HasDiskConflict = true;
            return await AskAsync(tab, writeOnOverwrite: false) == DiskConflictChoice.Overwrite;
        }
        tab.HasDiskConflict = false;
        return true;
    }

    // ---- resolving --------------------------------------------------------------------------------

    /// <summary>Put the question to the user, once at a time per tab, and carry out the answer.
    /// <paramref name="writeOnOverwrite"/> is false when the caller is about to write the file itself.</summary>
    private async Task<DiskConflictChoice> AskAsync(EditorTabViewModel tab, bool writeOnOverwrite)
    {
        if (tab.ScriptPath is not { } path || !_asking.Add(tab)) return DiskConflictChoice.Decide;
        try
        {
            var choice = _dialogs is null
                ? DiskConflictChoice.Decide
                : await _dialogs.ConfirmDiskConflictAsync(Path.GetFileName(path));

            switch (choice)
            {
                case DiskConflictChoice.Reload:
                    // Read again: the dialog may have been up for a while.
                    if (await ReadAsync(path) is { } now) Reload(tab, now);
                    else _ctx.SetStatus($"{Path.GetFileName(path)} is no longer on disk; the tab kept its text.");
                    break;

                case DiskConflictChoice.Overwrite:
                    tab.HasDiskConflict = false;
                    if (!writeOnOverwrite) break;
                    var text = tab.Text;
                    try
                    {
                        await _ctx.ScriptStore.WriteTextAsync(path, text, CancellationToken.None);
                        tab.MarkSaved(text);
                        _ctx.SetStatus($"Saved {Path.GetFileName(path)} over the change on disk.");
                    }
                    catch (Exception ex)
                    {
                        tab.HasDiskConflict = true;
                        _ctx.SetStatus($"Could not save {Path.GetFileName(path)}: {ex.Message}");
                    }
                    break;

                default:
                    _ctx.SetStatus($"{Path.GetFileName(path)} changed on disk and has unsaved edits — it won't be "
                                   + "written until you choose. Save asks again.");
                    break;
            }
            return choice;
        }
        finally
        {
            _asking.Remove(tab);
        }
    }

    /// <summary>Take the file's text as the tab's own, keeping the caret where it can.</summary>
    private static void Reload(EditorTabViewModel tab, string disk)
    {
        var caret = tab.CaretOffset;
        tab.MarkSaved(disk);   // baseline first, so the Text change below reads as clean
        tab.Text = disk;
        tab.HasDiskConflict = false;
        tab.CaretOffset = Math.Clamp(caret, 0, disk.Length);
    }

    private async Task<string?> ReadAsync(string path)
    {
        if (!_ctx.ScriptStore.FileExists(path)) return null;
        try { return await _ctx.ScriptStore.ReadTextAsync(path, CancellationToken.None); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    // ---- watching ---------------------------------------------------------------------------------

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncWatchers();

    /// <summary>One watcher per directory that holds an open tab's file; on the UI thread. Best-effort: a
    /// directory that cannot be watched (inotify limits, a vanished folder) is still checked when the window
    /// comes forward and before every write.</summary>
    public void SyncWatchers()
    {
        if (_disposed) return;
        var paths = _ctx.AllTabs.Select(t => t.ScriptPath).OfType<string>().Select(Full).ToHashSet(StringComparer.Ordinal);
        var directories = paths.Select(Path.GetDirectoryName).OfType<string>().ToHashSet(StringComparer.Ordinal);

        lock (_gate)
        {
            _paths = paths;
            foreach (var gone in _watchers.Keys.Where(d => !directories.Contains(d)).ToList())
            {
                _watchers[gone].Dispose();
                _watchers.Remove(gone);
            }
            foreach (var directory in directories.Where(d => !_watchers.ContainsKey(d)))
            {
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    var watcher = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
                    watcher.Created += (_, e) => OnFileEvent(e.FullPath);
                    watcher.Deleted += (_, e) => OnFileEvent(e.FullPath);
                    // An editor that saves by writing a temp file and renaming it over the original shows
                    // up only as a rename *to* the tab's path.
                    watcher.Renamed += (_, e) => { OnFileEvent(e.FullPath); OnFileEvent(e.OldFullPath); };
                    watcher.EnableRaisingEvents = true;
                    _watchers[directory] = watcher;
                }
                catch
                {
                    // Unwatchable; the activation and pre-write checks still cover it.
                }
            }
        }
    }

    /// <summary>The paths being watched, for tests.</summary>
    internal IReadOnlyCollection<string> WatchedDirectories
    {
        get { lock (_gate) return _watchers.Keys.ToList(); }
    }

    private void OnFileEvent(string path)
    {
        bool ours;
        lock (_gate) ours = _paths.Contains(Full(path));
        if (ours) Schedule();
    }

    /// <summary>Check everything once the burst of events settles — one save is several events.</summary>
    private void Schedule()
    {
        if (_disposed) return;
        lock (_gate)
        {
            _timer ??= new Timer(_ => _post(() => CrashReporter.Observe(CheckAllAsync(), "script disk check")));
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx.Tabs.CollectionChanged -= OnTabsChanged;
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            _timer?.Dispose();
            _timer = null;
        }
    }
}
