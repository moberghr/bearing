namespace Bearing.Core.Data;

/// <summary>
/// Reach the database through an SSH server: Bearing opens an SSH session to <see cref="Host"/>, forwards a
/// loopback port to the connection's <c>Host:Port</c> <b>as the SSH server sees it</b>, and the driver
/// connects to that loopback port.
/// <para>
/// No secret lives here. The SSH password — or the private key's passphrase — is asked for at connect time
/// and kept in <c>ISecretStore</c> under a separate id (<c>SshSecretId</c>), exactly like the database
/// password (§1.1). The key file itself is referenced by path and never copied.
/// </para>
/// </summary>
public sealed record SshTunnel
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string User { get; init; } = "";

    /// <summary>How to log in to the SSH server.</summary>
    public SshAuthKind Auth { get; init; } = SshAuthKind.Password;

    /// <summary>Private key file for <see cref="SshAuthKind.PrivateKey"/> (OpenSSH or PEM; <c>~</c> expanded at
    /// connect time). Null or blank = the first of <c>~/.ssh/id_ed25519</c>, <c>id_ecdsa</c>, <c>id_rsa</c>
    /// that exists, as <c>ssh</c> would try. Ignored for password authentication.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// The SSH server's host key as OpenSSH prints it (<c>SHA256:…</c>), pinned the first time it was seen
    /// — OpenSSH's <c>StrictHostKeyChecking=accept-new</c>. A different key is refused, never trusted; null
    /// means not seen yet. Not part of <see cref="SameRoute"/>: pinning it changes nothing about the route.
    /// </summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>True when a key file, not a password, authenticates.</summary>
    public bool UsesPrivateKey => Auth == SshAuthKind.PrivateKey;

    /// <summary>Whether two tunnel settings reach the server the same way — everything but the pinned host
    /// key. Null equals null (both direct).</summary>
    public static bool SameRoute(SshTunnel? a, SshTunnel? b)
        => (a is null && b is null)
           || (a is not null && b is not null
               && a with { HostKeyFingerprint = null } == b with { HostKeyFingerprint = null });
}

/// <summary>How an <see cref="SshTunnel"/> logs in. The secret — the password, or the key's passphrase — is in
/// the keychain either way, never in the project.</summary>
public enum SshAuthKind
{
    Password,
    PrivateKey,
}
