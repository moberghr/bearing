using Squirrel.Core.Workspace;

namespace Squirrel.Persistence;

/// <summary>Picks the OS keychain when a Secret Service is reachable, else the local file fallback.</summary>
public static class SecretStoreFactory
{
    public static async Task<ISecretStore> CreateAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux() && await SecretToolSecretStore.IsAvailableAsync(ct))
            return new SecretToolSecretStore();

        // TODO: MacKeychainStore / WindowsDpapiStore on those platforms.
        return new FileFallbackSecretStore();
    }
}
