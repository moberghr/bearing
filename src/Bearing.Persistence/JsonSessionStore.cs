using System.Text.Json;
using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>Per-user session under <c>&lt;project&gt;/.bearing/session.json</c> (gitignored).</summary>
public sealed class JsonSessionStore : ISessionStore
{
    private static string SessionPath(string projectDir) =>
        Path.Combine(projectDir, ".bearing", "session.json");

    public async Task<SessionState?> LoadAsync(string projectDirectory, CancellationToken ct)
    {
        var path = SessionPath(projectDirectory);
        if (!File.Exists(path)) return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SessionState>(stream, BearingJson.Options, ct);
    }

    public async Task SaveAsync(string projectDirectory, SessionState state, CancellationToken ct)
    {
        var dir = Path.Combine(projectDirectory, ".bearing");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.json");
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, state, BearingJson.Options, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public void Save(string projectDirectory, SessionState state)
    {
        var dir = Path.Combine(projectDirectory, ".bearing");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.json");
        // Atomic tmp+move (mirrors SaveAsync): this runs on the shutdown path where interruption is
        // likeliest, and a truncated session.json would lose the resume state.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, BearingJson.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
