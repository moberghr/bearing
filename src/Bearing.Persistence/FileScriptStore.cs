using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>Filesystem-backed <see cref="IScriptStore"/> — the project's scripts folder on disk.</summary>
public sealed class FileScriptStore : IScriptStore
{
    public ScriptTree? ReadTree(string scriptsDirectory)
        => Directory.Exists(scriptsDirectory) ? ReadFolder(scriptsDirectory) : null;

    private static ScriptTree ReadFolder(string dir)
    {
        var folders = Directory.EnumerateDirectories(dir)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(ReadFolder)
            .ToList();
        var files = Directory.EnumerateFiles(dir, "*.sql")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ScriptFileRef(p, Path.GetFileName(p)))
            .ToList();
        return new ScriptTree(dir, Path.GetFileName(dir), folders, files);
    }

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Task<string> ReadTextAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(path, ct);

    public async Task WriteTextAsync(string path, string text, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
    }

    public void CreateFolder(string path) => Directory.CreateDirectory(path);

    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);

    public void Delete(string path) => File.Delete(path);   // no-op when it's already gone
}
