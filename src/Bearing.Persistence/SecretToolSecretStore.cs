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

    public async Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
    {
        var r = await RunAsync(
            ["store", "--label", $"Bearing connection {connectionId}", "app", App, "connection", connectionId.ToString()],
            stdin: password, ct).ConfigureAwait(false);
        if (r.Exit != 0)
            throw new InvalidOperationException($"secret-tool store failed: {r.Detail}");
    }

    public async Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
    {
        var r = await RunAsync(
            ["lookup", "app", App, "connection", connectionId.ToString()], stdin: null, ct).ConfigureAwait(false);
        if (r.Exit != 0) return null;               // not found
        return r.Stdout.TrimEnd('\n');
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
        if (await GetPasswordAsync(connectionId, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException(
                $"secret-tool clear failed — the password is still in the keyring: {r.Detail}");
    }

    private static Task<CliResult> RunAsync(string[] args, string? stdin, CancellationToken ct) =>
        CliRunner.RunAsync("secret-tool", args, stdin, ct);
}
