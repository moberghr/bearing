namespace Bearing.Persistence;

/// <summary>
/// Removes the on-disk secret files written by the old file fallback (base64 under
/// <c>&lt;data dir&gt;/secrets/</c>), which builds up to 2026-08-19 could be opted into when no OS keyring
/// was available.
/// <para>
/// That opt-in is gone: passwords now live in the OS credential store or nowhere. Leaving the old files
/// behind would keep recoverable passwords on disk that nothing writes to, reads, or offers to clear — so
/// they are deleted at startup, once, on every platform. The connections that used them fall back to
/// prompting (<see cref="NoSecretStore"/>), which is what a keyring-less machine does anyway.
/// </para>
/// </summary>
public static class LegacySecretFiles
{
    /// <summary>The directory the old fallback wrote to.</summary>
    public static string DefaultDirectory => Path.Combine(BearingPaths.DataDir, "secrets");

    /// <summary>
    /// Delete every leftover secret file and the directory itself. Best effort (§5.2): a file that can't be
    /// removed is logged, not thrown — startup must not fail over cleanup.
    /// </summary>
    /// <returns>How many files were deleted; 0 when there was nothing there.</returns>
    public static int Purge(string? dir = null)
    {
        var path = dir ?? DefaultDirectory;
        if (!Directory.Exists(path)) return 0;

        var deleted = 0;
        string? failure = null;
        foreach (var file in Directory.GetFiles(path))
        {
            try { File.Delete(file); deleted++; }
            // The name is a connection id, never a secret, but it isn't worth logging either — the count is.
            catch (Exception ex) { failure ??= ex.Message; }
        }

        // Only when it's empty: a directory that still holds something unexpected stays put.
        try { Directory.Delete(path); } catch { /* not empty, or in use — the files are what mattered */ }

        if (deleted > 0 || failure is not null)
            CrashLog.Note("secret-store", failure is null
                ? $"Deleted {deleted} legacy secret file(s) from the removed on-disk fallback."
                : $"Deleted {deleted} legacy secret file(s); at least one could not be removed: {failure}");

        return deleted;
    }
}
