using System;
using Bearing.Core.Data;

namespace Bearing.Sessions;

/// <summary>A resolved secret ready to hand to the provider as the connection password, plus an optional
/// expiry. <see cref="ExpiresAt"/> is set for short-lived credentials (Entra tokens) and null for a fixed
/// password / prompted password — it drives proactive disconnect-before-expiry in
/// <see cref="ConnectionSessionManager"/>.
/// <para>
/// <see cref="User"/> is the login name resolved alongside the secret, for a connection that does not
/// store one (<see cref="ConnectionInfo.UserFromEntraLogin"/>); null means "use the connection's own".
/// It is cached and refreshed with the token, so the two never describe different identities.
/// </para></summary>
public sealed record Credential(string? Secret, DateTimeOffset? ExpiresAt, string? User = null)
{
    /// <summary><paramref name="info"/> as it should be connected with this credential: the resolved user
    /// written over the stored one when there is one, otherwise unchanged. Every route that builds a factory
    /// from a resolved credential goes through this, so the app, the CLI and the dialog's Test log in as the
    /// same identity.</summary>
    public ConnectionInfo ApplyTo(ConnectionInfo info)
        => string.IsNullOrWhiteSpace(User) ? info : info with { User = User };
}
