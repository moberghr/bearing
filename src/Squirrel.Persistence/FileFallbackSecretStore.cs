using System.Runtime.InteropServices;
using System.Text;
using Squirrel.Core.Workspace;

namespace Squirrel.Persistence;

/// <summary>
/// Fallback for machines without a Secret Service: per-connection files in the user data dir
/// (0600 where supported). Keeps secrets out of the SHARED project.json, but is NOT the OS keychain —
/// <see cref="IsSecure"/> is false so the UI can warn.
/// </summary>
public sealed class FileFallbackSecretStore : ISecretStore
{
    private readonly string _dir;
    public bool IsSecure => false;

    public FileFallbackSecretStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(SquirrelPaths.DataDir, "secrets");
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(Guid id) => Path.Combine(_dir, id.ToString("N"));

    public async Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
    {
        var path = PathFor(connectionId);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        await File.WriteAllTextAsync(path, encoded, ct);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public async Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
    {
        var path = PathFor(connectionId);
        if (!File.Exists(path)) return null;
        var encoded = await File.ReadAllTextAsync(path, ct);
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    public Task DeleteAsync(Guid connectionId, CancellationToken ct)
    {
        var path = PathFor(connectionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
