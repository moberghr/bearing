using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// With no OS credential store there is nowhere for a password to go, and no opt-in to change that: the
/// store refuses the write, keeps nothing, and reports why it couldn't be used. The old on-disk fallback
/// (base64 under the data dir, opt-in via a setting) was removed on 2026-08-19 — these tests are what stops
/// it coming back in some other shape.
/// </summary>
public class SecretStorePolicyTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Refuses_to_write_and_stores_nothing()
    {
        var store = new NoSecretStore();
        var id = Guid.NewGuid();

        Assert.False(store.CanStore);
        Assert.False(store.IsSecure);

        var ex = await Assert.ThrowsAsync<SecretStorageRefusedException>(
            () => store.SetPasswordAsync(id, "s3cr3t!", Ct));
        Assert.Contains("keyring", ex.Message);
        // The message has to name the way out, because there is no "save it anyway" any more.
        Assert.Contains("Prompt each time", ex.Message);

        Assert.Null(await store.GetPasswordAsync(id, Ct));
    }

    [Fact]
    public async Task Reads_find_nothing_and_deletes_are_a_no_op()
    {
        var store = new NoSecretStore();

        Assert.Null(await store.GetPasswordAsync(Guid.NewGuid(), Ct));
        await store.DeleteAsync(Guid.NewGuid(), Ct);   // clearing a password that was never stored is fine
    }

    [Fact]
    public void The_probe_reason_is_carried_for_the_UI_to_explain_itself()
    {
        // Why this exists: the warnings used to assert "No system keyring found" without having checked.
        Assert.Equal("secret-tool: no such collection",
            new NoSecretStore("secret-tool: no such collection").UnavailableReason);
        Assert.Null(new NoSecretStore().UnavailableReason);
    }

    [Fact]
    public async Task The_factory_never_returns_a_store_that_writes_outside_the_keychain()
    {
        // On a box with a keyring this is the platform store, which stores by definition; without one it is
        // NoSecretStore. Either way "can store" and "is secure" must agree — a store that writes passwords
        // somewhere weaker is exactly what no longer exists.
        var store = await SecretStoreFactory.CreateAsync();

        Assert.Equal(store.IsSecure, store.CanStore);
        if (!store.IsSecure) Assert.IsType<NoSecretStore>(store);
    }
}
