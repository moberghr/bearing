using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.Core.Data;

namespace Squirrel.App.ViewModels;

/// <summary>
/// One editor tab: its own buffer, backing script (or scratch), caret, and last result. Execution
/// and connection are shared at the shell level; a tab just holds editor + result state.
/// </summary>
public sealed partial class EditorTabViewModel : ObservableObject
{
    private readonly string _scratchName;

    public EditorTabViewModel(string scratchName, string text = "", string? scriptPath = null)
    {
        _scratchName = scratchName;
        _text = text;
        _scriptPath = scriptPath;
        UpdateHeader();
    }

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private int _caretOffset;
    [ObservableProperty] private string? _scriptPath;   // absolute path, or null for a scratch buffer
    [ObservableProperty] private QueryResult? _lastResult;
    [ObservableProperty] private string _header = "";

    /// <summary>True while backed by an unsaved scratch buffer.</summary>
    public bool IsScratch => ScriptPath is null;

    partial void OnScriptPathChanged(string? value) => UpdateHeader();

    private void UpdateHeader()
        => Header = ScriptPath is null ? _scratchName : Path.GetFileName(ScriptPath);
}
