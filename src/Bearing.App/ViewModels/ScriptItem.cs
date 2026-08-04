using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bearing.App.ViewModels;

/// <summary>One saved SQL script shown in the side pane's Scripts tree.</summary>
public sealed record ScriptItem(string Name, string FullPath)
{
    /// <summary>True when an open tab backs this file and has unsaved edits (snapshot at refresh time).</summary>
    public bool IsUnsaved { get; init; }
}

/// <summary>A folder (subdirectory of the scripts dir) in the Scripts tree. Holds subfolders and
/// scripts (mixed, matched by type in the TreeView), so the tree nests to any depth.</summary>
public sealed partial class ScriptFolderViewModel : ObservableObject
{
    public ScriptFolderViewModel(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    public string Name { get; }
    public string FullPath { get; }

    /// <summary>Subfolders (<see cref="ScriptFolderViewModel"/>) then scripts (<see cref="ScriptItem"/>).</summary>
    public ObservableCollection<object> Children { get; } = new();

    /// <summary>Total scripts under this folder, recursively (shown right-aligned).</summary>
    [ObservableProperty] private int _count;

    [ObservableProperty] private bool _isExpanded = true;
}
