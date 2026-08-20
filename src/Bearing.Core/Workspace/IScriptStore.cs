namespace Bearing.Core.Workspace;

/// <summary>An immutable snapshot of one scripts-folder subtree: nested subfolders then <c>.sql</c> files,
/// each already sorted. A pure data view so the scripts view-model can build its tree without touching I/O.</summary>
public sealed record ScriptTree(
    string Path,
    string Name,
    IReadOnlyList<ScriptTree> Folders,
    IReadOnlyList<ScriptFileRef> Files);

/// <summary>A <c>.sql</c> file in the scripts tree. <c>SizeBytes</c> comes free with the
/// directory scan and is what lets a content search skip a file it should never read (0 = unknown).</summary>
public sealed record ScriptFileRef(string Path, string Name, long SizeBytes = 0);

/// <summary>
/// Filesystem access for the project's scripts folder, behind an interface so the scripts / workspace
/// view-models never touch <c>System.IO</c> directly. Path-keyed (scripts live at real paths that editor
/// tabs reference). Mutating operations throw on I/O error so the caller can surface the message.
/// </summary>
public interface IScriptStore
{
    /// <summary>Snapshot the folder tree rooted at <paramref name="scriptsDirectory"/>; null if it doesn't exist.</summary>
    ScriptTree? ReadTree(string scriptsDirectory);

    bool FileExists(string path);
    bool DirectoryExists(string path);

    Task<string> ReadTextAsync(string path, CancellationToken ct);

    /// <summary>Write <paramref name="text"/> to <paramref name="path"/>, creating the parent directory if needed.</summary>
    Task WriteTextAsync(string path, string text, CancellationToken ct);

    /// <summary>Create the folder at <paramref name="path"/> (and any missing parents).</summary>
    void CreateFolder(string path);

    /// <summary>Move/rename a file.</summary>
    void Move(string sourcePath, string destinationPath);

    /// <summary>Delete a script file. Irreversible — the caller confirms first. A path that isn't there is
    /// not an error: the goal state ("this file is gone") already holds.</summary>
    void Delete(string path);
}
