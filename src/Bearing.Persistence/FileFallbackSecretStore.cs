using System.Runtime.InteropServices;
using System.Text;
using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>
/// Fallback for machines without a Secret Service: per-connection files in the user data dir
/// (0600 where supported). Keeps secrets out of the SHARED project.json, but is NOT the OS keychain —
/// <see cref="IsSecure"/> is false so the UI can warn.
/// <para>
/// <b>Writing is opt-in.</b> Base64 on disk is plaintext with extra steps, so by default this store
/// refuses to take a new password (<see cref="CanStore"/> false) and connections are expected to prompt
/// and hold the secret in memory instead. Reading and deleting always work, so secrets written before the
/// opt-in was turned off keep resolving and can still be cleared.
/// </para>
/// </summary>
public sealed class FileFallbackSecretStore : ISecretStore
{
    private readonly string _dir;
    private readonly Func<bool> _allowStore;

    public bool IsSecure => false;

    /// <summary>Read live, so toggling the setting takes effect without a restart (the settings window
    /// applies edits immediately, and a store that lied about this would be worse than useless).</summary>
    public bool CanStore => _allowStore();

    /// <param name="dir">Where the per-connection files live; defaults to the user data dir.</param>
    /// <param name="allowStore">Whether writing is permitted right now — the user's opt-in, read on every
    /// call. Defaults to always-allowed so tests and callers that own the directory keep working.</param>
    public FileFallbackSecretStore(string? dir = null, Func<bool>? allowStore = null)
    {
        _dir = dir ?? Path.Combine(BearingPaths.DataDir, "secrets");
        _allowStore = allowStore ?? (static () => true);
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(Guid id) => Path.Combine(_dir, id.ToString("N"));

    public async Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
    {
        if (!CanStore)
            throw new SecretStorageRefusedException(
                "No system keyring is available, so this password was not saved. Enable "
                + "Settings ▸ Security ▸ \"Store passwords on disk when no keyring is available\" to save it "
                + "unencrypted, or use the \"Prompt each time\" credential kind.");

        var path = PathFor(connectionId);
        var tmp = path + ".tmp";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        // Write to a temp file, lock it down, then atomically rename into place. A crash mid-write
        // leaves the previous secret intact (never a truncated file), and restricting the mode BEFORE
        // the rename means the secret is never briefly world-readable (the old order chmod'd after).
        await File.WriteAllTextAsync(tmp, encoded, ct).ConfigureAwait(false);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }

    public async Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
    {
        var path = PathFor(connectionId);
        if (!File.Exists(path)) return null;
        var encoded = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    public Task DeleteAsync(Guid connectionId, CancellationToken ct)
    {
        var path = PathFor(connectionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
