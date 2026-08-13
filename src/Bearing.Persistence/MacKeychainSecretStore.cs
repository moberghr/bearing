using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>
/// OS keychain on macOS: a generic password item in the user's login keychain, via the built-in
/// <c>security</c> CLI. Keyed by service = the app dir name and account = the connection guid, mirroring
/// the {app, connection} attribute pair the Linux store uses — <see cref="BearingPaths.AppDirName"/>
/// carries the <c>BEARING_PROFILE</c> isolation, so a dev profile never reads the installed app's secrets.
/// No file of ours is involved, and the item is visible and revocable in Keychain Access.
/// <para>
/// <b>Why the CLI rather than Security.framework:</b> a keychain item's ACL is bound to the code signature
/// of the app that created it. Bearing is not signed with a stable identity yet, so items created by the
/// app itself would provoke an "allow access?" dialog after every rebuild — and after every auto-update
/// (see the Velopack roadmap item). Items created through <c>/usr/bin/security</c> are owned by that
/// Apple-signed binary, which does not change, so reads stay silent.
/// </para>
/// <para>
/// <b>Known trade-off:</b> <c>security add-generic-password</c> takes the password as an argument, so it is
/// briefly visible in the process list. Its prompt-on-stdin mode is not usable here — it reads from
/// <c>/dev/tty</c> when one exists and would hang a terminal-launched app — and macOS restricts reading
/// another process's arguments to the same user, who can already read the keychain item itself. Nothing is
/// written to disk or to a log; see §1.1.
/// </para>
/// </summary>
public sealed class MacKeychainSecretStore : ISecretStore
{
    private static string Service => BearingPaths.AppDirName;

    public bool IsSecure => true;

    /// <summary>Always: the keychain is where a password belongs, so there's nothing to opt into.</summary>
    public bool CanStore => true;

    public async Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
    {
        var r = await RunAsync(
            [
                "add-generic-password",
                // Replace an existing item for the same service+account. Without -U a second store of the
                // same connection's password fails as a duplicate instead of rotating it.
                "-U",
                "-a", connectionId.ToString(),
                "-s", Service,
                "-l", $"Bearing connection {connectionId}",
                "-w", password,
            ], ct).ConfigureAwait(false);
        if (r.Exit != 0)
            throw new InvalidOperationException($"security add-generic-password failed: {r.Detail}");
    }

    public async Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
    {
        // -w prints the password alone on stdout. A non-zero exit is "no password to hand back" — item not
        // found (44) or a keychain we can't read — and the caller then prompts instead of failing.
        var r = await RunAsync(
            ["find-generic-password", "-a", connectionId.ToString(), "-s", Service, "-w"], ct).ConfigureAwait(false);
        if (r.Exit != 0) return null;
        return r.Stdout.TrimEnd('\n');
    }

    public async Task DeleteAsync(Guid connectionId, CancellationToken ct)
    {
        var r = await RunAsync(
            ["delete-generic-password", "-a", connectionId.ToString(), "-s", Service], ct).ConfigureAwait(false);
        if (r.Exit == 0) return;

        // Same reasoning as the libsecret store: the tool exits non-zero for "no such item" *and* for a real
        // failure (a locked keychain), so the status alone can't settle it. Check the postcondition instead —
        // gone means the delete did its job; still there means the caller must not be told the credential
        // was removed when it wasn't.
        if (await GetPasswordAsync(connectionId, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException(
                $"security delete-generic-password failed — the password is still in the keychain: {r.Detail}");
    }

    private static Task<CliResult> RunAsync(string[] args, CancellationToken ct) =>
        CliRunner.RunAsync("security", args, stdin: null, ct);
}
