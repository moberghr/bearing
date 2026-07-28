using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Squirrel.App.ViewModels;

/// <summary>
/// One editor tab: its own buffer, backing script (or scratch), caret, chosen connection, and last
/// result. Each tab runs against its own <see cref="ConnectionId"/>; the shell resolves that to a
/// live session at execution time.
/// </summary>
public sealed partial class EditorTabViewModel : ObservableObject
{
    public EditorTabViewModel(string displayName, string text = "", string? scriptPath = null)
    {
        _displayName = displayName;
        _text = text;
        _savedText = text;   // opened/created content is the clean baseline
        _scriptPath = scriptPath;
        UpdateHeader();
    }

    /// <summary>The content last persisted to disk (for a script) — the baseline for dirty detection.</summary>
    private string _savedText;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private int _caretOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _scriptPath;   // absolute path, or null for a scratch buffer

    /// <summary>True when the buffer differs from the last-saved content (drives the modified marker).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _isModified;

    /// <summary>Modified indicator applies to file-backed scripts (scratch buffers are always unsaved).</summary>
    public bool IsDirty => !IsScratch && IsModified;

    partial void OnTextChanged(string value) => IsModified = value != _savedText;

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

    /// <summary>True while this tab has a query / page / count / save in flight. Drives the tab-header
    /// running indicator and (for the selected tab) the Run/Cancel button and Esc.</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>Begin a run: publish the busy flag and a fresh cancellation source, returning its token.
    /// The caller has already ensured this tab isn't already running (one operation per tab).</summary>
    internal CancellationToken BeginRun()
    {
        _runCts = new CancellationTokenSource();
        IsRunning = true;
        return _runCts.Token;
    }

    /// <summary>End the current run: dispose the cancellation source and lower the busy flag.</summary>
    internal void EndRun()
    {
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

    /// <summary>True while backed by an unsaved scratch buffer.</summary>
    public bool IsScratch => ScriptPath is null;

    partial void OnScriptPathChanged(string? value) => UpdateHeader();
    partial void OnDisplayNameChanged(string value) => UpdateHeader();

    private void UpdateHeader()
        => Header = ScriptPath is null ? DisplayName : Path.GetFileName(ScriptPath);
}
