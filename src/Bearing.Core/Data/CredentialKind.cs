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

    /// <summary>Obtain a short-lived Microsoft Entra access token (used as the Postgres password) and
    /// refresh it when it nears expiry.</summary>
    EntraToken = 2,
}
