namespace Bearing.Persistence;

/// <summary>
/// How many times a credential-store operation is worth retrying, and when it isn't.
/// <para>
/// <b>Why this exists at all:</b> a *healthy* keyring intermittently refuses a secret transfer. Measured
/// 2026-08-13 on a working libsecret box — 250 scripted store→read→delete cycles produced 3 failures (~1.2%),
/// every one of them <c>secret-tool: Couldn't create item: The secret was transferred or encrypted in an
/// invalid way.</c> The rate is characteristic of the Secret Service session-encryption handshake rather than
/// of anything this app does, so the failure is a per-call dice roll: independent between attempts, and
/// therefore fixable by simply asking again.
/// </para>
/// <para>
/// That matters far beyond a retry being nice to have. The startup probe treats one refusal as "this machine
/// has no credential store" and demotes the whole session to <see cref="NoSecretStore"/>, which <b>stores no
/// passwords at all</b> — so a 1-in-80 dice roll was silently costing users their keychain. Three attempts
/// takes ~1.2% to ~0.015%.
/// </para>
/// <para>
/// <b>There is a second, quieter fault that a retry alone cannot see</b>, found the same day by
/// <c>PlatformKeychainTests</c>: about 1 rotation in 200 (measured over 200 store-over-an-existing-item
/// cycles) exits <c>0</c> with an <i>empty</i> stderr and still leaves nothing in the keyring. Nothing reports
/// an error, so the only defence is checking the postcondition —
/// <see cref="SecretToolSecretStore.SetPasswordAsync"/> reads its own write back, which is why it does not use
/// the plain retry wrapper.
/// </para>
/// </summary>
internal static class SecretRetry
{
    /// <summary>Total attempts, not retries-after-the-first. 3 is where the residual stops mattering.</summary>
    public const int Attempts = 3;

    /// <summary>
    /// True when a failure should <b>not</b> be retried because the user already answered. A locked collection
    /// makes the helper raise an unlock prompt, so retrying a dismissed prompt would ask two more times — the
    /// one case where "just try again" is worse than reporting the failure. Everything else retries: the
    /// measured fault is random per call, and a genuinely permanent error costs only a few fast process
    /// launches to re-confirm.
    /// </summary>
    public static bool LooksFinal(string? detail)
        => detail is not null
        && (detail.Contains("dismissed", StringComparison.OrdinalIgnoreCase)
         || detail.Contains("cancel", StringComparison.OrdinalIgnoreCase));
}
