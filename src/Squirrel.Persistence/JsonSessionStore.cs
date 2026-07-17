using System.Text.Json;
using Squirrel.Core.Workspace;

namespace Squirrel.Persistence;

/// <summary>Per-user session under <c>&lt;project&gt;/.squirrel/session.json</c> (gitignored).</summary>
public sealed class JsonSessionStore : ISessionStore
{
    private static string SessionPath(string projectDir) =>
        Path.Combine(projectDir, ".squirrel", "session.json");

    public async Task<SessionState?> LoadAsync(string projectDirectory, CancellationToken ct)
    {
        var path = SessionPath(projectDirectory);
        if (!File.Exists(path)) return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SessionState>(stream, SquirrelJson.Options, ct);
    }

    public async Task SaveAsync(string projectDirectory, SessionState state, CancellationToken ct)
    {
        var dir = Path.Combine(projectDirectory, ".squirrel");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.json");
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, state, SquirrelJson.Options, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public void Save(string projectDirectory, SessionState state)
    {
        var dir = Path.Combine(projectDirectory, ".squirrel");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.json");
        File.WriteAllText(path, JsonSerializer.Serialize(state, SquirrelJson.Options));
    }
}
