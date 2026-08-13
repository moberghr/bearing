using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>Picks this platform's OS credential store when one is actually reachable, else the local file
/// fallback (which by default refuses to store anything — see <see cref="FileFallbackSecretStore"/>).</summary>
public static class SecretStoreFactory
{
    private const string ProbeValue = "probe";

    /// <param name="allowUnencryptedFile">Whether the file fallback may write passwords to disk — the user's
    /// opt-in, read live so the setting applies without a restart. Null means "not allowed", which is the
    /// default posture: no keyring means no stored password, and connections prompt instead.</param>
    public static async Task<ISecretStore> CreateAsync(
        Func<bool>? allowUnencryptedFile = null, CancellationToken ct = default)
    {
        if (await CreatePlatformStoreAsync(ct).ConfigureAwait(false) is { } keychain) return keychain;

        return new FileFallbackSecretStore(allowStore: allowUnencryptedFile ?? (static () => false));
    }

    /// <summary>
    /// This platform's OS credential store, or null when there isn't one or it doesn't work here. Public so
    /// tests can exercise whichever real store the host machine has — the Windows and macOS implementations
    /// can only be verified by running on those platforms (§4.2's skip-safe pattern).
    /// </summary>
    public static async Task<ISecretStore?> CreatePlatformStoreAsync(CancellationToken ct = default)
    {
        var store = PlatformStore();
        if (store is null) return null;
        return await ProbeAsync(store, ct).ConfigureAwait(false) ? store : null;
    }

    /// <summary>Which store this OS would use, before asking whether it works.</summary>
    private static ISecretStore? PlatformStore()
    {
        if (OperatingSystem.IsLinux()) return new SecretToolSecretStore();
        if (OperatingSystem.IsWindows()) return new WindowsCredentialSecretStore();
        if (OperatingSystem.IsMacOS()) return new MacKeychainSecretStore();
        return null;    // anything else (BSD, a container image without either) — the file fallback decides.
    }

    /// <summary>
    /// Store → read back → delete a throwaway secret. Every platform store wraps something that can be
    /// absent (no <c>secret-tool</c> installed), asleep (a locked keyring), or refused (no logon session for
    /// the Credential Manager), and none of them reliably says so up front — so availability is decided by
    /// actually using it once. The cost is one write at startup; the alternative is discovering the problem
    /// when the user tries to connect.
    /// </summary>
    private static async Task<bool> ProbeAsync(ISecretStore store, CancellationToken ct)
    {
        var probe = Guid.NewGuid();
        try
        {
            await store.SetPasswordAsync(probe, ProbeValue, ct).ConfigureAwait(false);
            return await store.GetPasswordAsync(probe, ct).ConfigureAwait(false) == ProbeValue;
        }
        catch
        {
            return false;
        }
        finally
        {
            // Clean up even when the read failed, so a probe never lingers in the user's keychain — on
            // Windows and macOS it would be visible in the OS credential UI, which looks like a bug.
            // Deleting a secret that was never written is a no-op in all three stores.
            try { await store.DeleteAsync(probe, ct).ConfigureAwait(false); } catch { /* best effort */ }
        }
    }
}
