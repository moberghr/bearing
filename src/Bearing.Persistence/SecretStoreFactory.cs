using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>The outcome of asking this platform's credential store whether it works: the store when it does,
/// and — when it doesn't — the reason it was rejected plus the name of what rejected it. <see cref="Store"/>
/// and <see cref="Failure"/> are mutually exclusive; exactly one is set.</summary>
public readonly record struct PlatformStoreProbe(ISecretStore? Store, string? Failure, string? StoreName = null);

/// <summary>Picks this platform's OS credential store when one is actually reachable, else
/// <see cref="NoSecretStore"/> — which keeps nothing. There is no on-disk fallback and nothing to opt into
/// (removed 2026-08-19): a password Bearing can't hand to the keychain is prompted for and held in memory.</summary>
public static class SecretStoreFactory
{
    private const string ProbeValue = "probe";

    public static async Task<ISecretStore> CreateAsync(CancellationToken ct = default)
    {
        var probe = await ProbePlatformStoreAsync(ct).ConfigureAwait(false);
        if (probe.Store is { } keychain) return keychain;

        // Record *why*, and carry the same reason into the store so the UI can show it. Without this the app
        // reports one confident "no system keyring" for four quite different situations — no helper on PATH,
        // no session bus, a locked collection, or a store that took the write and handed something else back
        // — and the user has no way to tell them apart.
        if (probe.Failure is { } why)
            CrashLog.Note("secret-store", $"Storing no passwords ({probe.StoreName ?? "no platform store"}): {why}");

        return new NoSecretStore(probe.Failure);
    }

    /// <summary>
    /// This platform's OS credential store, or null when there isn't one or it doesn't work here. Public so
    /// tests can exercise whichever real store the host machine has — the Windows and macOS implementations
    /// can only be verified by running on those platforms (§4.2's skip-safe pattern).
    /// </summary>
    public static async Task<ISecretStore?> CreatePlatformStoreAsync(CancellationToken ct = default)
        => (await ProbePlatformStoreAsync(ct).ConfigureAwait(false)).Store;

    /// <summary>
    /// This platform's credential store, and — when it was rejected — a redacted reason why. Separate from
    /// <see cref="CreatePlatformStoreAsync"/> because "there is no keychain here" and "the keychain is right
    /// there but refused us" are the same answer to the caller and completely different answers to the user.
    /// </summary>
    public static async Task<PlatformStoreProbe> ProbePlatformStoreAsync(CancellationToken ct = default)
    {
        var store = PlatformStore();
        if (store is null)
            return new PlatformStoreProbe(null, "no OS credential store is implemented for this platform.");

        var failure = await ProbeFailureAsync(store, ct).ConfigureAwait(false);
        // The rejecting store's name rides alongside the reason rather than being glued onto the front of it:
        // the reason is shown to the user, and "SecretToolSecretStore probe failed" is log text, not UI text.
        return failure is null
            ? new PlatformStoreProbe(store, null)
            : new PlatformStoreProbe(null, failure, store.GetType().Name);
    }

    /// <summary>Which store this OS would use, before asking whether it works.</summary>
    private static ISecretStore? PlatformStore()
    {
        if (OperatingSystem.IsLinux()) return new SecretToolSecretStore();
        if (OperatingSystem.IsWindows()) return new WindowsCredentialSecretStore();
        if (OperatingSystem.IsMacOS()) return new MacKeychainSecretStore();
        return null;    // anything else (BSD, a container image without either) — nothing is stored there.
    }

    /// <summary>
    /// Store → read back → delete a throwaway secret. Every platform store wraps something that can be
    /// absent (no <c>secret-tool</c> installed), asleep (a locked keyring), or refused (no logon session for
    /// the Credential Manager), and none of them reliably says so up front — so availability is decided by
    /// actually using it once. The cost is one write at startup; the alternative is discovering the problem
    /// when the user tries to connect.
    /// </summary>
    /// <returns>Null when the store works; otherwise a short, redacted reason it was rejected.</returns>
    /// <remarks>Internal rather than private so the rejection reasons and the cleanup postcondition can be
    /// driven by a fake store — the real ones can only be exercised on their own platform (§4.2).</remarks>
    internal static async Task<string?> ProbeFailureAsync(ISecretStore store, CancellationToken ct)
    {
        // Retried because one refusal is not evidence of a missing keychain: a healthy keyring rejects roughly
        // 1 transfer in 80 (see SecretRetry). Deciding "no credential store here" on a single sample of that is
        // what put a working-libsecret machine on the password-refusing no-store posture for a whole session.
        // Attempts are independent — fresh probe id, fresh helper process — so the last failure is the honest
        // one to report.
        string? failure = null;
        for (var attempt = 1; attempt <= SecretRetry.Attempts; attempt++)
        {
            failure = await ProbeOnceAsync(store, ct).ConfigureAwait(false);
            if (failure is null) return null;
            if (SecretRetry.LooksFinal(failure)) break;   // the user dismissed an unlock prompt; don't nag
        }
        return failure;
    }

    /// <summary>One store → read back → delete cycle. See <see cref="ProbeFailureAsync"/> for why it runs more
    /// than once.</summary>
    private static async Task<string?> ProbeOnceAsync(ISecretStore store, CancellationToken ct)
    {
        var probe = Guid.NewGuid();
        try
        {
            await store.SetPasswordAsync(probe, ProbeValue, ct).ConfigureAwait(false);
            var readBack = await store.GetPasswordAsync(probe, ct).ConfigureAwait(false);
            return readBack == ProbeValue ? null
                 : readBack is null ? "it accepted the probe secret and then read it back as missing."
                 : "it read the probe secret back as a different value.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutting down — not a verdict on the keychain, and must not burn the remaining attempts
        }
        catch (Exception ex)
        {
            // The stores already put the helper's own stderr / error code in their messages, which is the part
            // worth having. Redacted anyway (§1.1): nothing here should ever contain a real password — the
            // probe value is the literal "probe" — but this text goes to a file, so don't rely on that alone.
            return Bearing.Core.Data.SafeErrorText.Of(ex);
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
