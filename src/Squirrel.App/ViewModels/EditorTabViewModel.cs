using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>Result sets from the last execution — one per statement in a multi-statement run.
    /// Each holds its own mutable row buffer so paging can append without a rebuild.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastResult))]
    private IReadOnlyList<ResultSetViewModel> _results = Array.Empty<ResultSetViewModel>();

    /// <summary>Convenience: the first result set (or null), for single-statement callers.</summary>
    public ResultSetViewModel? LastResult => Results.Count > 0 ? Results[0] : null;

    [ObservableProperty] private string _header = "";

    /// <summary>Scratch display label ("Scratch N" or a user rename); ignored once backed by a file.</summary>
    [ObservableProperty] private string _displayName;

    /// <summary>The connection this tab executes against; null means "no connection chosen".</summary>
    [ObservableProperty] private Guid? _connectionId;

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
