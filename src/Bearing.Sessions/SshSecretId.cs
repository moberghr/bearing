using System;
using System.Security.Cryptography;
using System.Text;

namespace Bearing.Sessions;

/// <summary>
/// The secret-store key for a connection's SSH password / key passphrase. <c>ISecretStore</c> is keyed by
/// one Guid per secret and the connection id is already the database password's, so the SSH secret gets a
/// name-based Guid derived from it: deterministic (nothing extra to persist or to carry across the
/// clipboard — a paste mints a new connection id and so a new SSH id), distinct from every connection id.
/// </summary>
public static class SshSecretId
{
    public static Guid For(Guid connectionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("bearing.ssh/" + connectionId.ToString("D")));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);   // version 5 (name-based)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(bytes);
    }
}
