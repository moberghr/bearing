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

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SessionState>(stream, BearingJson.Options, ct).ConfigureAwait(false);
        }
        // A truncated or hand-edited session.json used to throw straight out of project open, so a corrupt
        // *cache* of window state stopped the project from opening at all. Session state is disposable by
        // definition — fall back to "no session" (defaults + one empty tab), matching AppSettingsStore.
        // Cancellation is the caller's business and still propagates.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string projectDirectory, SessionState state, CancellationToken ct)
    {
        var dir = Path.Combine(projectDirectory, ".bearing");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.json");
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, state, BearingJson.Options, ct).ConfigureAwait(false);
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
