using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Re-asking whether this machine has a usable keychain. The startup probe runs once, extremely early, and a
/// keyring that wasn't serving yet at that instant used to pin the app into storing nothing for the whole
/// session — refusing to save passwords, warning that no keyring exists, with no way to re-check but a
/// restart. That is a real Linux case (reported 2026-08-13 on a machine whose libsecret worked perfectly).
/// <para>
/// The direction matters as much as the mechanism: this upgrades and never downgrades, so a transient failure
/// can't demote a session that already has a working keychain (§1.1).
/// </para>
/// </summary>
public class SecretStorageRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-secrets", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm() => new(
        new FakeProvider(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FakeSecretStore(),
        settings: SettingsService.InMemory(new AppSettings()));

    private static FakeSecretStore NoKeyring => new() { IsSecure = false, CanStore = false };
    private static FakeSecretStore Keychain => new() { IsSecure = true, CanStore = true };

    [Fact]
    public void The_posture_carries_why_the_store_is_unusable()
    {
        // The connection dialog turns this into advice (SecretStorageAdviceTests); without it the warning is
        // back to asserting a cause nobody checked.
        var vm = NewVm();
        vm.AttachSecretStore(new FakeSecretStore
        {
            IsSecure = false,
            CanStore = false,
            UnavailableReason = "secret-tool: the prompt was dismissed",
        });

        Assert.Equal("secret-tool: the prompt was dismissed", vm.SecretStorage.Reason);

        vm.AttachSecretStore(Keychain);
        Assert.Null(vm.SecretStorage.Reason);   // nothing to explain when it works
    }

    [Fact]
    public async Task A_re_probe_that_still_finds_nothing_refreshes_the_reason()
    {
        // The posture didn't improve, so nothing is announced — but the dialog is about to explain *why*
        // passwords can't be saved, and the startup reason may be stale by now.
        var vm = NewVm();
        var later = new FakeSecretStore
        {
            IsSecure = false,
            CanStore = false,
            UnavailableReason = "secret-tool: the prompt was dismissed",
        };
        vm.AttachSecretStore(
            new FakeSecretStore { IsSecure = false, CanStore = false, UnavailableReason = "no session bus" },
            reprobe: _ => Task.FromResult<ISecretStore>(later));

        Assert.False(await vm.RefreshSecretStorageAsync());   // still no keychain: nothing improved

        Assert.False(vm.SecretStorage.CanStore);
        Assert.Equal("secret-tool: the prompt was dismissed", vm.SecretStorage.Reason);
    }

    [Fact]
    public async Task A_keychain_that_appears_after_startup_is_adopted()
    {
        var vm = NewVm();
        var keychain = Keychain;
        vm.AttachSecretStore(NoKeyring, reprobe: _ => Task.FromResult<ISecretStore>(keychain));

        Assert.False(vm.SecretStorageSecure);              // what the user saw: "no system keyring"
        Assert.False(vm.SecretStorage.CanStore);

        Assert.True(await vm.RefreshSecretStorageAsync());

        Assert.True(vm.SecretStorageSecure);
        Assert.True(vm.SecretStorage.CanStore);            // the dialog will now offer to store a password
        Assert.Contains("keychain", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_adopted_keychain_is_the_store_the_rest_of_the_app_uses()
    {
        // The swap has to reach the credential path, not just the two display properties — connection saving
        // reads WorkspaceContext.Secrets, which is why the store is held in one place behind a live read.
        var vm = NewVm();
        var keychain = Keychain;
        vm.AttachSecretStore(NoKeyring, reprobe: _ => Task.FromResult<ISecretStore>(keychain));
        var id = Guid.NewGuid();
        await vm.OpenProjectAsync(Path.Combine(_root, "proj"));   // saving a connection needs a project

        await vm.RefreshSecretStorageAsync();
        await vm.Connections.AddOrUpdateConnectionAsync(
            new Bearing.Core.Data.ConnectionInfo
            {
                Id = id,
                Name = "c",
                ProviderId = "postgres",
                Host = "h",
                Database = "d",
                User = "u",
            },
            password: "stored-after-upgrade");

        Assert.Equal("stored-after-upgrade", await keychain.GetPasswordAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task A_session_already_on_a_keychain_never_re_probes()
    {
        // Not just "doesn't downgrade" — it must not even ask, so this stays a cheap no-op on the UI path.
        var vm = NewVm();
        var probes = 0;
        vm.AttachSecretStore(Keychain, reprobe: _ => { probes++; return Task.FromResult<ISecretStore>(NoKeyring); });

        Assert.False(await vm.RefreshSecretStorageAsync());

        Assert.Equal(0, probes);
        Assert.True(vm.SecretStorageSecure);
    }

    [Fact]
    public async Task A_probe_that_still_finds_no_keyring_changes_nothing()
    {
        var vm = NewVm();
        vm.AttachSecretStore(NoKeyring, reprobe: _ => Task.FromResult<ISecretStore>(NoKeyring));

        Assert.False(await vm.RefreshSecretStorageAsync());

        Assert.False(vm.SecretStorageSecure);
    }

    [Fact]
    public async Task A_throwing_probe_is_swallowed_and_leaves_the_posture_alone()
    {
        // Best-effort like every other probe on this path: re-checking must never take the app down or
        // corrupt a posture the user is currently relying on.
        var vm = NewVm();
        vm.AttachSecretStore(NoKeyring, reprobe: _ => throw new InvalidOperationException("bus went away"));

        Assert.False(await vm.RefreshSecretStorageAsync());

        Assert.False(vm.SecretStorageSecure);
    }

    [Fact]
    public async Task With_no_reprobe_attached_refreshing_is_a_no_op()
    {
        // AttachSecretStore's second argument is optional, so every existing caller (and test) keeps working.
        var vm = NewVm();
        vm.AttachSecretStore(NoKeyring);

        Assert.False(await vm.RefreshSecretStorageAsync());

        Assert.False(vm.SecretStorageSecure);
    }
}
