using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// The contract every OS credential store must satisfy, run against whatever real store *this* host has:
/// libsecret on Linux, the Credential Manager on Windows, the login keychain on macOS. The three
/// implementations are the same interface over completely different mechanics (a CLI on stdin, a Win32 API,
/// a CLI on argv), and none of them can be verified from another platform — so these are
/// <see cref="SkippableFactAttribute"/>s over <see cref="SecretStoreFactory.CreatePlatformStoreAsync"/>
/// (§4.2) and running <c>dotnet test</c> on a Windows or macOS box is what actually checks those two.
/// <para>
/// These tests write to the developer's real keychain, so every one cleans up after itself under
/// <c>finally</c> and uses a fresh <see cref="Guid"/> per case (never a fixed id, which two concurrent test
/// runs would fight over).
/// </para>
/// </summary>
public class PlatformKeychainTests
{
    private static async Task<ISecretStore> RequireStoreAsync()
    {
        var store = await SecretStoreFactory.CreatePlatformStoreAsync(CancellationToken.None);
        Skip.If(store is null, "No OS credential store is reachable on this machine.");
        return store!;
    }

    private static CancellationToken Ct => CancellationToken.None;

    [SkippableFact]
    public async Task The_platform_store_is_this_platforms_implementation_and_reports_itself_secure()
    {
        var store = await RequireStoreAsync();

        // Guards against the quiet failure this work exists to fix: falling through to the file fallback on a
        // platform that does have a credential store.
        if (OperatingSystem.IsLinux()) Assert.IsType<SecretToolSecretStore>(store);
        else if (OperatingSystem.IsWindows()) Assert.IsType<WindowsCredentialSecretStore>(store);
        else if (OperatingSystem.IsMacOS()) Assert.IsType<MacKeychainSecretStore>(store);

        Assert.True(store.IsSecure);    // drives the status bar and the connection dialog's warnings
        Assert.True(store.CanStore);    // a real keychain has nothing to opt into

        // …and the factory hands the same store to the app, rather than the fallback.
        var chosen = await SecretStoreFactory.CreateAsync();
        Assert.Equal(store.GetType(), chosen.GetType());
    }

    [SkippableFact]
    public async Task A_password_round_trips_and_rotates_in_place()
    {
        var store = await RequireStoreAsync();
        var id = Guid.NewGuid();
        try
        {
            await store.SetPasswordAsync(id, "keychain-pw", Ct);
            Assert.Equal("keychain-pw", await store.GetPasswordAsync(id, Ct));

            // Rotation, i.e. storing over an existing secret. This is where the platforms differ most:
            // `security add-generic-password` fails as a duplicate without -U, while CredWrite replaces.
            await store.SetPasswordAsync(id, "rotated-pw", Ct);
            Assert.Equal("rotated-pw", await store.GetPasswordAsync(id, Ct));
        }
        finally
        {
            await store.DeleteAsync(id, Ct);
        }

        Assert.Null(await store.GetPasswordAsync(id, Ct));
    }

    /// <summary>
    /// Deleting has to be verified rather than trusted: <c>secret-tool clear</c> and
    /// <c>security delete-generic-password</c> both exit non-zero for a real failure *and* for "nothing
    /// matched", so neither trusting nor ignoring the exit code is right. Clearing a credential that isn't
    /// there is a success for the caller either way — it's how a connection stops storing a password.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_is_idempotent_and_deleting_a_secret_that_was_never_stored_does_not_throw()
    {
        var store = await RequireStoreAsync();
        var id = Guid.NewGuid();

        await store.SetPasswordAsync(id, "to-be-deleted", Ct);
        Assert.Equal("to-be-deleted", await store.GetPasswordAsync(id, Ct));

        await store.DeleteAsync(id, Ct);
        Assert.Null(await store.GetPasswordAsync(id, Ct));

        await store.DeleteAsync(id, Ct);                // second delete: nothing matches, not a failure
        await store.DeleteAsync(Guid.NewGuid(), Ct);    // never stored at all
    }

    [SkippableFact]
    public async Task An_unstored_connection_reads_as_null_and_connections_do_not_share_a_secret()
    {
        var store = await RequireStoreAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            Assert.Null(await store.GetPasswordAsync(a, Ct));

            await store.SetPasswordAsync(a, "pw-a", Ct);
            await store.SetPasswordAsync(b, "pw-b", Ct);

            // Keying is per-connection: on Windows that's the target name, elsewhere the account attribute.
            Assert.Equal("pw-a", await store.GetPasswordAsync(a, Ct));
            Assert.Equal("pw-b", await store.GetPasswordAsync(b, Ct));

            // Deleting one leaves the other alone.
            await store.DeleteAsync(a, Ct);
            Assert.Null(await store.GetPasswordAsync(a, Ct));
            Assert.Equal("pw-b", await store.GetPasswordAsync(b, Ct));
        }
        finally
        {
            await store.DeleteAsync(a, Ct);
            await store.DeleteAsync(b, Ct);
        }
    }

    /// <summary>
    /// A real Postgres password can hold anything. This is the encoding/quoting check: UTF-16 conversion for
    /// the Credential Manager blob, and shell-free argument passing for the two CLI stores (an argument list
    /// rather than a command line, so quotes and backslashes are never re-parsed).
    /// <para>
    /// Known limit, deliberately not asserted because it differs by platform: the CLI-backed stores read the
    /// password from the helper's stdout and strip the trailing newline, so a password *ending* in a newline
    /// does not survive them. Interior whitespace does.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_awkward_password_round_trips_byte_for_byte()
    {
        var store = await RequireStoreAsync();
        const string awkward = "p@ss \"w'o$rd\" \\ % ; | & 100% Ωμέγα 日本語 🔐 ünïcødé";
        var id = Guid.NewGuid();
        try
        {
            await store.SetPasswordAsync(id, awkward, Ct);
            Assert.Equal(awkward, await store.GetPasswordAsync(id, Ct));
        }
        finally
        {
            await store.DeleteAsync(id, Ct);
        }
    }
}
