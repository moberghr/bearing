using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.Core.Data;

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
        _scriptPath = scriptPath;
        UpdateHeader();
    }

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private int _caretOffset;
    [ObservableProperty] private string? _scriptPath;   // absolute path, or null for a scratch buffer
    [ObservableProperty] private QueryResult? _lastResult;
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
