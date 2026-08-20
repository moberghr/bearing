using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bearing.App.ViewModels;

/// <summary>
/// One saved SQL script shown in the side pane's Scripts tree. An observable object rather than a record
/// because the row is editable in place — renaming turns the label into a text box (#39), which is state the
/// row itself has to carry.
/// </summary>
public sealed partial class ScriptItem : ObservableObject
{
    public ScriptItem(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
        _renameDraft = Path.GetFileNameWithoutExtension(name);
    }

    public string Name { get; }
    public string FullPath { get; }

    /// <summary>True when an open tab backs this file and has unsaved edits (snapshot at refresh time).</summary>
    public bool IsUnsaved { get; init; }

    /// <summary>
    /// The line that made this file a hit for the filter's <em>contents</em>, shown under the name — or null
    /// when the row is here because its name matched, or because nothing is being filtered. A content hit
    /// that looked identical to a name hit read as noise: this is what says why the file is in the list (#47).
    /// </summary>
    public string? MatchLine { get; init; }

    /// <summary>True while this row is an editable box rather than a label.</summary>
    [ObservableProperty] private bool _isRenaming;

    /// <summary>
    /// True while this script is the one being dragged. The row dims, so it's visible <em>what</em> is in
    /// flight — the cursor can't say it (the platform owns the pointer during a drag) and the drop highlight
    /// only says where it would land.
    /// </summary>
    [ObservableProperty] private bool _isDragging;

    /// <summary>The name being typed. Seeded from the file's own name, without the <c>.sql</c> — the
    /// extension is not the user's to retype, and <c>RenameScriptAsync</c> puts it back.</summary>
    [ObservableProperty] private string _renameDraft;

    /// <summary>Start editing the name in place, from whatever it is called now.</summary>
    public void BeginRename()
    {
        RenameDraft = Path.GetFileNameWithoutExtension(Name);
        IsRenaming = true;
    }
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

    /// <summary>
    /// True while a dragged script is hovering this folder, so the row can say where the drop will land —
    /// the move worked before this, it just gave no sign of its target until you let go.
    /// </summary>
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>True for the project's scratch folder — the one folder the app owns rather than the user.
    /// Pinned to the top of the tree and styled apart so it reads as a holding pen, not a curated folder.</summary>
    public bool IsScratch { get; init; }
}
