using Bearing.App.Services;

namespace Bearing.App.Connections;

/// <summary>Why no credential store was available, as far as the probe's reason can tell.</summary>
public enum SecretStorageCause
{
    /// <summary>Something refused, and it isn't one of the cases below — show what it said.</summary>
    Unknown,

    /// <summary>This OS has no credential store Bearing implements. Nothing to fix here.</summary>
    NoPlatformStore,

    /// <summary>The keyring is there but locked, or its unlock prompt was dismissed. Worth an unlock hint.</summary>
    Locked,

    /// <summary>The helper binary the store shells out to isn't installed.</summary>
    HelperMissing,
}

/// <summary>
/// Turns a probe failure into what the connection editor tells the user.
/// <para>
/// The warnings used to assert <i>"No system keyring found"</i>, which was known to be wrong in a real case:
/// the keyring was installed and answering, the probe just ran before it was serving. The probe now reports
/// why it rejected a store, and this maps that to advice — a locked collection, a missing helper and a
/// platform with no store at all read identically as "no keyring" and want different things done about them.
/// </para>
/// Pure and stateless so it can be tested without a UI or a keychain (§2.5).
/// </summary>
public static class SecretStorageAdvice
{
    /// <summary>Classify a probe reason (<see cref="SecretStoragePosture.Reason"/>). Matching is on the
    /// helper's own words, so an unrecognised message falls through to <see cref="SecretStorageCause.Unknown"/>
    /// and is shown verbatim rather than being explained away.</summary>
    public static SecretStorageCause Classify(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return SecretStorageCause.Unknown;

        if (Has(reason, "no OS credential store")) return SecretStorageCause.NoPlatformStore;

        // A dismissed prompt is the one failure the retry loop refuses to repeat (SecretRetry.LooksFinal),
        // and it is exactly the case where the user can fix it in five seconds — check it before the rest.
        if (Has(reason, "dismissed") || Has(reason, "cancel") || Has(reason, "locked"))
            return SecretStorageCause.Locked;

        if (Has(reason, "not found") || Has(reason, "No such file") || Has(reason, "not recognized")
            || Has(reason, "ENOENT"))
            return SecretStorageCause.HelperMissing;

        return SecretStorageCause.Unknown;
    }

    /// <summary>
    /// The full warning shown under a stored-password connection when nothing can be stored: what will
    /// happen, then why it happened. One string because it is one amber block.
    /// </summary>
    public static string NoStorageWarning(string? reason)
    {
        const string headline =
            "⚠ No system keyring could be reached — this password will NOT be saved. Bearing asks for it when "
            + "you connect and keeps it in memory for this session only. Choose \"Prompt each time\" to make "
            + "that explicit.";

        return Explanation(reason) is { } why ? headline + "\n\n" + why : headline;
    }

    /// <summary>The "why" line, or null when there is nothing honest to say.</summary>
    public static string? Explanation(string? reason) => Classify(reason) switch
    {
        SecretStorageCause.NoPlatformStore =>
            "This platform has no credential store Bearing can use.",
        SecretStorageCause.Locked =>
            "Your keyring looks locked (or the unlock prompt was dismissed). Unlock it and reopen this "
            + "dialog — Bearing re-checks every time it opens.",
        SecretStorageCause.HelperMissing =>
            "The keyring helper isn't installed. On Linux that's secret-tool (package libsecret-tools or "
            + "libsecret); reopen this dialog once it's on PATH.",
        // Reason unknown: quote it instead of inventing a cause — that mistake is what this class exists for.
        _ => string.IsNullOrWhiteSpace(reason) ? null : "The credential store reported: " + reason.Trim(),
    };

    private static bool Has(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
