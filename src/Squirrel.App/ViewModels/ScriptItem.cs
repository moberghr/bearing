using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Squirrel.App.ViewModels;

/// <summary>One saved SQL script shown in the side pane's Scripts tree.</summary>
public sealed record ScriptItem(string Name, string FullPath)
{
    /// <summary>True when an open tab backs this file and has unsaved edits (snapshot at refresh time).</summary>
    public bool IsUnsaved { get; init; }
}

/// <summary>A folder (subdirectory of the scripts dir) grouping scripts in the Scripts tree.</summary>
public sealed partial class ScriptFolderViewModel : ObservableObject
{
    public ScriptFolderViewModel(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    public string Name { get; }
    public string FullPath { get; }
    public ObservableCollection<ScriptItem> Scripts { get; } = new();
    public int Count => Scripts.Count;

    [ObservableProperty] private bool _isExpanded = true;
}
