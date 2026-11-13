using System.Globalization;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>
/// The connection dialog's SSH tab as pure logic (§2.5): typed text in, <see cref="SshTunnel"/> out. The
/// pinned host key is carried, not typed — it changes only by being seen (accept-new) or forgotten.
/// </summary>
public static class SshTunnelForm
{
    public const int DefaultPort = 22;

    /// <summary>The tunnel the boxes describe, or null when the box is unticked. A blank or unreadable port
    /// is 22; a blank key path (key auth) means ssh's default keys, and the path is dropped for password auth.
    /// A route change drops the pin — the key it pinned belonged to another server.</summary>
    public static SshTunnel? Build(bool enabled, string? host, string? port, string? user, SshAuthKind auth,
        string? keyPath, SshTunnel? existing, bool forgetHostKey)
    {
        if (!enabled)
        {
            return null;
        }
        var built = new SshTunnel
        {
            Host = (host ?? "").Trim(),
            Port = int.TryParse((port ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535
                ? p : DefaultPort,
            User = (user ?? "").Trim(),
            Auth = auth,
            PrivateKeyPath = auth == SshAuthKind.PrivateKey && !string.IsNullOrWhiteSpace(keyPath) ? keyPath.Trim() : null,
        };
        var keepPin = !forgetHostKey && existing is not null
                      && existing.Host == built.Host && existing.Port == built.Port;
        return keepPin ? built with { HostKeyFingerprint = existing!.HostKeyFingerprint } : built;
    }

    /// <summary>The secret to save for the tunnel: the password box for password auth, the passphrase box for
    /// a key (empty = unencrypted key), and empty — which deletes any stored one — when there is no tunnel.</summary>
    public static string SecretToStore(SshTunnel? tunnel, string? password, string? passphrase)
        => tunnel is null ? "" : (tunnel.UsesPrivateKey ? passphrase : password) ?? "";

    /// <summary>What Save hands on: <see cref="SecretToStore"/>, or null — leave the keychain alone — when it
    /// would change nothing: the secret is the one the dialog opened with, or there was never a tunnel.</summary>
    public static string? SecretToSave(SshTunnel? tunnel, SshTunnel? existingTunnel, string? existingSecret,
        string? password, string? passphrase)
    {
        if (tunnel is null && existingTunnel is null)
        {
            return null;
        }
        var secret = SecretToStore(tunnel, password, passphrase);
        return secret == (existingSecret ?? "") ? null : secret;
    }

    /// <summary>What a ticked tab is missing, or null when it can connect.</summary>
    public static string? Problem(SshTunnel? tunnel)
        => tunnel is null ? null
           : string.IsNullOrWhiteSpace(tunnel.Host) ? "SSH host is required."
           : string.IsNullOrWhiteSpace(tunnel.User) ? "SSH user is required."
           : null;
}
