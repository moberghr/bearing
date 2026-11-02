using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Workspace;

namespace Bearing.App.Workspace;

/// <summary>
/// The script store, plus one fact the rest of it doesn't keep: which files Bearing is writing <i>right
/// now</i>. A write truncates and then fills, so a look at the file in between sees neither the old text nor
/// the new one — and <see cref="ScriptFileSync"/> would call that a change made by someone else. Every write
/// in the app goes through the context's store, so wrapping it here covers them all (autosave, Save, the
/// shutdown flush, copy into project) without each having to remember.
/// </summary>
public sealed class ScriptWriteTracker : IScriptStore
{
    private readonly IScriptStore _inner;
    private readonly Dictionary<string, int> _writing = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ScriptWriteTracker(IScriptStore inner) => _inner = inner;

    /// <summary>Whether a write to <paramref name="path"/> is under way.</summary>
    public bool IsWriting(string path)
    {
        var key = Key(path);
        lock (_gate) return _writing.ContainsKey(key);
    }

    public async Task WriteTextAsync(string path, string text, CancellationToken ct)
    {
        var key = Key(path);
        lock (_gate) _writing[key] = _writing.GetValueOrDefault(key) + 1;
        try
        {
            await _inner.WriteTextAsync(path, text, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_writing[key] <= 1) _writing.Remove(key);
                else _writing[key]--;
            }
        }
    }

    public ScriptTree? ReadTree(string scriptsDirectory) => _inner.ReadTree(scriptsDirectory);
    public bool FileExists(string path) => _inner.FileExists(path);
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public Task<string> ReadTextAsync(string path, CancellationToken ct) => _inner.ReadTextAsync(path, ct);
    public void CreateFolder(string path) => _inner.CreateFolder(path);
    public void Move(string sourcePath, string destinationPath) => _inner.Move(sourcePath, destinationPath);
    public void Delete(string path) => _inner.Delete(path);

    private static string Key(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
