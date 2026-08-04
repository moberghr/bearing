using System.Text.Json;
using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>Recent project directories, most-recent-first, in the app config dir.</summary>
public sealed class FileRecentProjects : IRecentProjects
{
    private const int MaxEntries = 20;
    private readonly string _path;

    public FileRecentProjects(string? path = null)
        => _path = path ?? Path.Combine(BearingPaths.ConfigDir, "recent.json");

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return Array.Empty<string>();
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<string>>(stream, BearingJson.Options, ct)
               ?? new List<string>();
    }

    public async Task AddAsync(string directory, CancellationToken ct)
    {
        var full = Path.GetFullPath(directory);
        var list = (await ListAsync(ct)).ToList();
        list.RemoveAll(p => string.Equals(p, full, StringComparison.Ordinal));
        list.Insert(0, full);
        if (list.Count > MaxEntries) list = list.Take(MaxEntries).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, list, BearingJson.Options, ct);
        File.Move(tmp, _path, overwrite: true);
    }
}
