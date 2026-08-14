using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>
/// OS keychain via the freedesktop Secret Service (libsecret's <c>secret-tool</c> CLI). Secrets are
/// keyed by attributes {app=bearing, connection=&lt;guid&gt;} and never touch any file on disk.
/// The password is handed over on stdin, never as an argument.
/// </summary>
public sealed class SecretToolSecretStore : ISecretStore
{
    // Matches the app dir name so a dev profile (BEARING_PROFILE) keeps its keychain entries
    // separate from the installed app's — same isolation as config/data dirs.
    private static string App => BearingPaths.AppDirName;
    public bool IsSecure => true;

    /// <summary>Always: the keychain is where a password belongs, so there's nothing to opt into.</summary>
    public bool CanStore => true;

    /// <summary>
    /// Store the password and <b>verify it is readable back</b> before reporting success — the same
    /// postcondition style as <see cref="DeleteAsync"/>, and for a measured reason. On healthy libsecret about
    /// 1 rotation in 200 exits <c>0</c> with an empty stderr and yet leaves nothing behind (measured
    /// 2026-08-13: 1 in 200 store-over-an-existing-item cycles). Trusting the exit code there tells the user
    /// their new password is saved while the keyring holds nothing, and the next connect quietly falls back to
    /// prompting. See <see cref="SecretRetry"/> for the separate ~1.2% fault that reports itself honestly.
    /// </summary>
    public async Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
    {
        string? detail = null;
        for (var attempt = 1; attempt <= SecretRetry.Attempts; attempt++)
        {
            var r = await RunOnceAsync(
                ["store", "--label", $"Bearing connection {connectionId}", "app", App, "connection", connectionId.ToString()],
                stdin: password, ct).ConfigureAwait(false);

            if (r.Exit != 0)
            {
                detail = r.Detail;
                if (SecretRetry.LooksFinal(r.Stderr)) break;   // the user dismissed the unlock prompt
                continue;
            }

            // Exit 0 is not enough (see above). A re-store is a plain replace, so retrying is safe.
            string? readBack;
            try
            {
                readBack = await GetPasswordAsync(connectionId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                detail = $"the write could not be confirmed: {ex.Message}";
                continue;
            }

            if (readBack == password) return;
            detail = "it accepted the password and then read it back as missing.";
        }

        // Never name the password itself here — this text reaches the status bar and crash.log (§1.1).
        throw new InvalidOperationException($"secret-tool store failed: {detail ?? "unknown error"}");
    }

    public async Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
    {
        var r = await RunAsync(
            ["lookup", "app", App, "connection", connectionId.ToString()], stdin: null, ct).ConfigureAwait(false);
        if (r.Exit == 0) return r.Stdout.TrimEnd('\n');

        // `lookup` exits 1 both for "no such item" and for a real failure, and only the first has an empty
        // stderr — the same ambiguity DeleteAsync documents below. Mapping *both* to null (as this did) makes a
        // keyring that failed to hand the password over indistinguishable from a connection that never had one:
        // the connect path then quietly goes out passwordless and prompts, for a password sitting in the
        // keyring the whole time. The transfer error behind that is ~1% per read, so it is a real event
        // (see SecretRetry), which is why it must be raised rather than absorbed.
        if (r.Stderr.Trim().Length == 0) return null;    // genuinely no secret stored for this connection
        throw new InvalidOperationException($"secret-tool lookup failed: {r.Detail}");
    }

    public async Task DeleteAsync(Guid connectionId, CancellationToken ct)
    {
        var r = await RunAsync(
            ["clear", "app", App, "connection", connectionId.ToString()], stdin: null, ct).ConfigureAwait(false);
        if (r.Exit == 0) return;

        // `clear` exits 1 for "nothing matched" *and* for a real failure (locked or absent keyring), with an
        // empty stderr in the first case — so the exit code alone can't be trusted either way. Check the
        // postcondition instead: if the secret is gone, the delete did its job; if it's still there, the
        // caller must hear about it rather than believe a credential was removed when it wasn't.
        string? still;
        try
        {
            still = await GetPasswordAsync(connectionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)   // the read itself failed, so the postcondition is unknown — say that, don't guess
        {
            throw new InvalidOperationException(
                $"secret-tool clear could not be confirmed — the password may still be in the keyring: {ex.Message}", ex);
        }

        if (still is not null)
            throw new InvalidOperationException(
                $"secret-tool clear failed — the password is still in the keyring: {r.Detail}");
    }

    /// <summary>
    /// Run <c>secret-tool</c>, retrying a failure that carries an error message. See <see cref="SecretRetry"/>:
    /// a healthy keyring refuses roughly one transfer in 80, so a single attempt turns that into a phantom
    /// missing password on read. Writes use <see cref="RunOnceAsync"/> instead — they own their retry loop
    /// because an exit code alone doesn't tell them whether they succeeded.
    /// </summary>
    private static async Task<CliResult> RunAsync(string[] args, string? stdin, CancellationToken ct)
    {
        var r = default(CliResult);
        for (var attempt = 1; attempt <= SecretRetry.Attempts; attempt++)
        {
            r = await CliRunner.RunAsync("secret-tool", args, stdin, ct).ConfigureAwait(false);
            if (r.Exit == 0 || !Retryable(r)) return r;
        }
        return r;
    }

    /// <summary>One attempt, no retry — for callers that judge success by more than the exit code.</summary>
    private static Task<CliResult> RunOnceAsync(string[] args, string? stdin, CancellationToken ct)
        => CliRunner.RunAsync("secret-tool", args, stdin, ct);

    /// <summary>A non-zero exit with an <b>empty</b> stderr is libsecret's "nothing matched" — a real answer,
    /// not a fault, so it is returned as-is rather than asked three times. A dismissed unlock prompt is the
    /// user's answer and equally final.</summary>
    private static bool Retryable(in CliResult r)
        => r.Stderr.Trim().Length > 0 && !SecretRetry.LooksFinal(r.Stderr);
}
