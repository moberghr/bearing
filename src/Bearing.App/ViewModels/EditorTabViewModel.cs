using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bearing.App.ViewModels;

/// <summary>
/// One editor tab: its own buffer, backing script (or scratch), caret, chosen connection, and last
/// result. Each tab runs against its own <see cref="ConnectionId"/>; the shell resolves that to a
/// live session at execution time.
/// </summary>
public sealed partial class EditorTabViewModel : ObservableObject
{
    public EditorTabViewModel(string displayName, string text = "", string? scriptPath = null, bool isScratch = false)
    {
        _displayName = displayName;
        _text = text;
        _savedText = text;   // opened/created content is the clean baseline
        _scriptPath = scriptPath;
        _isScratch = isScratch;
        UpdateHeader();
    }

    /// <summary>
    /// Directory of the project this tab belongs to, fixed for the tab's whole life. Switching projects
    /// parks tabs rather than closing them, so "the active project" is not the right answer for a tab's
    /// scratch folder, session entry or connection lookup — this is. Null only for a tab created before a
    /// project was loaded. Not bound; nothing displays it.
    /// </summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>The content last persisted to disk (for a script) — the baseline for dirty detection.</summary>
    private string _savedText;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private int _caretOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(HeaderTooltip))]
    private string? _scriptPath;   // absolute path; null until a scratch tab's file is created

    /// <summary>True when the buffer differs from the last-saved content (drives the modified marker).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(HasUnsavedWork))]
    private bool _isModified;

    /// <summary>Modified indicator applies to named scripts; scratch is autosaved, so it never shows one.</summary>
    public bool IsDirty => !IsScratch && IsModified;

    partial void OnTextChanged(string value)
    {
        IsModified = value != _savedText;
        OnPropertyChanged(nameof(HasUnsavedWork)); // scratch's answer depends on the text itself, not IsModified
    }

    /// <summary>Record <paramref name="savedText"/> as the clean baseline (on open/save) and recompute dirty state.</summary>
    public void MarkSaved(string savedText)
    {
        _savedText = savedText;
        IsModified = Text != _savedText;
    }
    /// <summary>Result sets currently displayed — one per statement in a multi-statement run.
    /// Each holds its own mutable row buffer so paging can append without a rebuild. FK navigation
    /// swaps this for the referenced result (previous frame stashed on <see cref="_resultHistory"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastResult))]
    private IReadOnlyList<ResultSetViewModel> _results = Array.Empty<ResultSetViewModel>();

    /// <summary>Convenience: the first result set (or null), for single-statement callers.</summary>
    public ResultSetViewModel? LastResult => Results.Count > 0 ? Results[0] : null;

    /// <summary>Frames displayed before the current one — grows on FK navigation, shrinks on Back.</summary>
    private readonly Stack<IReadOnlyList<ResultSetViewModel>> _resultHistory = new();

    /// <summary>True when FK navigation has stacked earlier results that Back can return to.</summary>
    public bool CanGoBack => _resultHistory.Count > 0;

    /// <summary>Show the results of a fresh run — clears any FK-navigation history.</summary>
    public void SetFreshResults(IReadOnlyList<ResultSetViewModel> results)
    {
        _resultHistory.Clear();
        Results = results;
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Stash the current results and display the FK-navigated ones on top.</summary>
    public void PushResults(IReadOnlyList<ResultSetViewModel> results)
    {
        _resultHistory.Push(Results);
        Results = results;
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Discard the current (navigated) results and restore the previous frame.</summary>
    public void GoBack()
    {
        if (_resultHistory.Count == 0) return;
        Results = _resultHistory.Pop();
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Swap the current result frame (e.g. after a save/discard refresh) without touching history.</summary>
    public void ReplaceResults(IReadOnlyList<ResultSetViewModel> results) => Results = results;

    // ---- execution state (per-tab) --------------------------------------------------------------
    // Each tab owns its own busy flag + cancellation source, so tabs run concurrently: every execute
    // opens its own pooled connection (NpgsqlDataSource is a thread-safe pool), and Run/Esc on the
    // focused tab never touch another tab's in-flight query. At most one operation runs per tab.
    private CancellationTokenSource? _runCts;

    /// <summary>Wall-clock of the current (or most recent) run, so the status bar can tick a live timer
    /// while the query is in flight. Started in <see cref="BeginRun"/>, stopped in <see cref="EndRun"/>.</summary>
    private readonly Stopwatch _runClock = new();

    /// <summary>Elapsed time of the current run (or the final duration once stopped) — drives the live
    /// status-bar execution timer for the selected tab.</summary>
    public TimeSpan RunElapsed => _runClock.Elapsed;

    /// <summary>True while this tab has a query / page / count / save in flight. Drives the tab-header
    /// running indicator and (for the selected tab) the Run/Cancel button and Esc.</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>Begin a run: publish the busy flag and a fresh cancellation source, returning its token.
    /// The caller has already ensured this tab isn't already running (one operation per tab).</summary>
    internal CancellationToken BeginRun()
    {
        _runCts = new CancellationTokenSource();
        _runClock.Restart();
        IsRunning = true;
        return _runCts.Token;
    }

    /// <summary>End the current run: dispose the cancellation source and lower the busy flag.</summary>
    internal void EndRun()
    {
        _runClock.Stop();
        _runCts?.Dispose();
        _runCts = null;
        IsRunning = false;
    }

    /// <summary>Cancel this tab's in-flight run, if any (Esc / the Run button as Cancel on the focused tab).</summary>
    public void CancelRun()
    {
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { /* completed between the null-check and Cancel */ }
    }

    [ObservableProperty] private string _header = "";

    /// <summary>Scratch display label ("Scratch N" or a user rename); ignored once backed by a file.</summary>
    [ObservableProperty] private string _displayName;

    /// <summary>The connection this tab executes against; null means "no connection chosen".</summary>
    [ObservableProperty] private Guid? _connectionId;

    /// <summary>Active database on the connection's server (may differ from the connection's default DB —
    /// the toolbar Database pill switches it). Null falls back to the connection's default.</summary>
    [ObservableProperty] private string? _databaseName;

    /// <summary>Denormalized connection name for tab-header display (set when the connection is assigned).</summary>
    [ObservableProperty] private string? _connectionDisplay;

    /// <summary>Denormalized environment badge color for the header; null = neutral.</summary>
    [ObservableProperty] private string? _connectionColor;

    /// <summary>
    /// True while this is a scratch buffer — an unnamed tab, whose file (once autosave creates one) lives
    /// in the project's scratch folder. Set by the workspace, not derived from <see cref="ScriptPath"/>:
    /// scratch tabs are file-backed now, so "has no path" no longer identifies them. Naming a scratch tab
    /// moves its file out of the scratch folder and clears this.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(HasUnsavedWork))]
    private bool _isScratch;

    /// <summary>
    /// True when closing this tab would lose work, and therefore must prompt. A file-backed script with
    /// unsaved edits qualifies; a scratch tab does not once autosave has put its text on disk — the file
    /// stays in the scratch folder after the tab closes. Scratch text that hasn't reached a file yet
    /// (autosave debounce still pending, or the write failed) is the one case left to protect.
    /// </summary>
    public bool HasUnsavedWork => IsScratch ? ScriptPath is null && Text.Trim().Length > 0 : IsDirty;

    partial void OnScriptPathChanged(string? value) { UpdateHeader(); OnPropertyChanged(nameof(HasUnsavedWork)); }
    partial void OnDisplayNameChanged(string value) => UpdateHeader();
    partial void OnIsScratchChanged(bool value) => UpdateHeader();

    /// <summary>A scratch tab shows its label ("Scratch 1"), not its generated <c>2026-08-06-01.sql</c>
    /// filename — the file is an implementation detail until the tab is named and promoted.</summary>
    private void UpdateHeader()
        => Header = IsScratch || ScriptPath is null ? DisplayName : Path.GetFileName(ScriptPath);

    /// <summary>
    /// Hover text for the tab title: which file on disk this tab actually is. A scratch tab's
    /// <see cref="Header"/> deliberately hides its filename, so this is the only place the link is visible
    /// (and the only way to tell two same-named scripts in different folders apart).
    /// <para>
    /// Project-relative when the file sits under the tab's own project — the absolute path is mostly
    /// <c>~/…/project/scripts/</c> noise — and absolute otherwise. Not recomputed on
    /// <see cref="ProjectDirectory"/>, which is fixed for the tab's life.
    /// </para>
    /// </summary>
    public string HeaderTooltip
        => ScriptPath is { } path ? DisplayPath(ProjectDirectory, path) : "Not saved to a file yet";

    /// <summary>Pure — path shown in <see cref="HeaderTooltip"/>. A file outside the project keeps its
    /// absolute path: <c>GetRelativePath</c> would answer with a <c>../..</c> walk, which reads worse.</summary>
    private static string DisplayPath(string? projectDirectory, string scriptPath)
    {
        if (string.IsNullOrEmpty(projectDirectory)) return scriptPath;
        var relative = Path.GetRelativePath(projectDirectory, scriptPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? scriptPath
            : relative;
    }
}
