using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// Writing to the file fallback is opt-in. Base64 under the data dir is plaintext with extra steps, so with
/// no keyring and no explicit consent the store refuses a new password instead of leaving a recoverable copy
/// on disk — while still reading and deleting whatever is already there.
/// </summary>
public class SecretStorePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-secret-policy", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    private string Dir => Path.Combine(_root, "secrets");

    [Fact]
    public async Task Refuses_to_write_when_storing_is_not_allowed_and_leaves_no_file()
    {
        var store = new FileFallbackSecretStore(Dir, allowStore: () => false);
        var id = Guid.NewGuid();

        Assert.False(store.CanStore);
        var ex = await Assert.ThrowsAsync<SecretStorageRefusedException>(
            () => store.SetPasswordAsync(id, "s3cr3t!", CancellationToken.None));
        Assert.Contains("keyring", ex.Message);

        // Nothing was written — not the secret, not a temp file.
        Assert.Empty(Directory.GetFiles(Dir));
        Assert.Null(await store.GetPasswordAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Stores_when_the_user_has_opted_in()
    {
        var store = new FileFallbackSecretStore(Dir, allowStore: () => true);
        var id = Guid.NewGuid();

        Assert.True(store.CanStore);
        await store.SetPasswordAsync(id, "s3cr3t!", CancellationToken.None);

        Assert.Equal("s3cr3t!", await store.GetPasswordAsync(id, CancellationToken.None));
        Assert.False(store.IsSecure); // opting in doesn't make it secure — the warning stays earned
    }

    [Fact]
    public async Task A_secret_written_while_opted_in_still_reads_and_deletes_after_opting_out()
    {
        var allow = true;
        var store = new FileFallbackSecretStore(Dir, allowStore: () => allow);
        var id = Guid.NewGuid();
        await store.SetPasswordAsync(id, "existing", CancellationToken.None);

        allow = false; // the setting is read live, so this takes effect immediately

        Assert.False(store.CanStore);
        // Existing installs don't lose access to what they already stored …
        Assert.Equal("existing", await store.GetPasswordAsync(id, CancellationToken.None));
        // … they just can't add more.
        await Assert.ThrowsAsync<SecretStorageRefusedException>(
            () => store.SetPasswordAsync(id, "replacement", CancellationToken.None));
        Assert.Equal("existing", await store.GetPasswordAsync(id, CancellationToken.None));

        // Clearing a secret is always allowed — that's the way *out* of the insecure store.
        await store.DeleteAsync(id, CancellationToken.None);
        Assert.Null(await store.GetPasswordAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task The_factory_defaults_to_refusing_when_no_opt_in_is_supplied()
    {
        // Only meaningful where the factory actually falls back (no reachable Secret Service). On a box with
        // a keyring this returns the keychain store, which stores by definition — assert per posture.
        var store = await SecretStoreFactory.CreateAsync();

        if (store.IsSecure) Assert.True(store.CanStore);
        else Assert.False(store.CanStore);
    }
}
