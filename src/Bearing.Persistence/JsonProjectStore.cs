using System.Text.Json;
using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>
/// A project is a directory: <c>project.json</c> (shareable manifest) + <c>scripts/</c> (SQL files).
/// </summary>
public sealed class JsonProjectStore : IProjectStore
{
    public const string ManifestFileName = "project.json";

    public async Task<Project> CreateAsync(string directory, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "scripts"));

        var project = new Project { Directory = directory, Manifest = new ProjectManifest { Name = name } };
        await SaveAsync(project, ct).ConfigureAwait(false);
        return project;
    }

    public async Task<Project> OpenAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No {ManifestFileName} in '{directory}'.", path);

        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<ProjectManifest>(stream, BearingJson.Options, ct).ConfigureAwait(false)
                       ?? new ProjectManifest();
        return new Project { Directory = directory, Manifest = manifest };
    }

    public async Task SaveAsync(Project project, CancellationToken ct)
    {
        Directory.CreateDirectory(project.Directory);
        Directory.CreateDirectory(project.ScriptsDirectory);

        var path = Path.Combine(project.Directory, ManifestFileName);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, project.Manifest, BearingJson.Options, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true); // atomic-ish replace
    }
}
