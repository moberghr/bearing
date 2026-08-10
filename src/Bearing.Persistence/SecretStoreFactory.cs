using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>Picks the OS keychain when a Secret Service is reachable, else the local file fallback.</summary>
public static class SecretStoreFactory
{
    /// <param name="allowUnencryptedFile">Whether the file fallback may write passwords to disk — the user's
    /// opt-in, read live so the setting applies without a restart. Null means "not allowed", which is the
    /// default posture: no keyring means no stored password, and connections prompt instead.</param>
    public static async Task<ISecretStore> CreateAsync(
        Func<bool>? allowUnencryptedFile = null, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux() && await SecretToolSecretStore.IsAvailableAsync(ct).ConfigureAwait(false))
            return new SecretToolSecretStore();

        // TODO: MacKeychainStore / WindowsDpapiStore on those platforms.
        return new FileFallbackSecretStore(allowStore: allowUnencryptedFile ?? (static () => false));
    }
}
