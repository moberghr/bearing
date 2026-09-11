namespace Bearing.Core.Data;

/// <summary>How a connection's secret (the value handed to the provider as the password) is obtained
/// at connect time. Persisted on <see cref="ConnectionInfo"/>; the default keeps every existing
/// connection on the classic stored-password behaviour.</summary>
public enum CredentialKind
{
    /// <summary>A fixed password kept in the secret store, keyed by connection id (the default).</summary>
    StoredPassword = 0,

    /// <summary>Prompt for the password at connect time. Held in memory for the app session only —
    /// never written to the secret store; re-prompted after restart / eviction / rejection.</summary>
    Prompt = 1,

    /// <summary>Obtain a short-lived Microsoft Entra access token and refresh it when it nears expiry. How
    /// the token reaches the server is the driver's business, not this enum's — Npgsql takes it as the
    /// password, SqlClient on the connection object — which is what
    /// <see cref="IDbProvider.SupportsEntraToken"/> answers per engine.</summary>
    EntraToken = 2,

    /// <summary>The OS identity of the running process (Windows / integrated authentication). There is
    /// no secret to obtain: the resolver returns a credential with a null secret, nothing is read from or
    /// written to the secret store, and the user is never prompted — so a connection on this kind must
    /// not warn about an unreachable keychain it does not use, and has nothing to refresh.</summary>
    Integrated = 3,
}
