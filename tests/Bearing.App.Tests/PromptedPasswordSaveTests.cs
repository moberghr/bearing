using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// A stored-password connection with nothing in the keychain (one just imported on a new machine) asks for
/// its password when its first connect is refused, and the answer is saved to the keychain — but only once a
/// connect with it has worked, so a mistyped password never reaches it. Driven through the real connect recipe
/// (<see cref="ConnectionSessionManager"/>), with the fake store and prompt.
/// </summary>
public class PromptedPasswordSaveTests
{
    private const string Typed = "typed-at-the-prompt";

    private static ConnectionInfo Conn(CredentialKind kind = CredentialKind.StoredPassword) => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod",
        ProviderId = "postgres",
        Host = "db",
        Database = "app",
        User = "karlo",
        CredentialKind = kind,
    };

    private sealed record Rig(
        FakeProvider Provider, CredentialResolver Resolver, ConnectionSessionManager Manager,
        FakeCredentialPrompt Prompt, List<string> Notices);

    private static Rig NewRig(ISecretStore? store, params string?[] answers)
    {
        var provider = new FakeProvider();
        var prompt = new FakeCredentialPrompt(answers);
        var resolver = new CredentialResolver(() => store, prompt, new FakeEntraTokens(_ => new Credential(null, null)));
        var notices = new List<string>();
        resolver.PasswordSaveNotice = (_, text) => { lock (notices) notices.Add(text); };
        var manager = new ConnectionSessionManager(provider, () => resolver, runSweepTimer: false);
        return new Rig(provider, resolver, manager, prompt, notices);
    }

    /// <summary>The first connect goes out passwordless and is refused; the retry — what the query path does
    /// after an authentication failure (Invalidate, then connect again) — asks.</summary>
    private static async Task RefusedThenRetriedAsync(Rig rig, ConnectionInfo info, bool retrySucceeds = true)
    {
        rig.Provider.TestResult = false;
        await Assert.ThrowsAsync<ConnectionFailedException>(() => rig.Manager.GetOrConnectAsync(info, CancellationToken.None));
        Assert.True(rig.Resolver.CanRefresh(info));   // so the query path retries at all
        rig.Resolver.Invalidate(info.Id);
        rig.Provider.TestResult = retrySucceeds;
        if (retrySucceeds) await rig.Manager.GetOrConnectAsync(info, CancellationToken.None);
        else await Assert.ThrowsAsync<ConnectionFailedException>(() => rig.Manager.GetOrConnectAsync(info, CancellationToken.None));
    }

    [Fact]
    public async Task A_password_typed_after_a_refused_first_connect_is_saved_once_it_works()
    {
        var store = new FakeSecretStore();
        var rig = NewRig(store, Typed);
        var info = Conn();

        await RefusedThenRetriedAsync(rig, info);

        Assert.Equal(1, rig.Prompt.Calls);
        Assert.Equal(Typed, rig.Provider.LastPassword);
        Assert.Equal(Typed, await store.GetPasswordAsync(info.Id, CancellationToken.None));
        var notice = Assert.Single(rig.Notices);
        Assert.Contains("Saved the password for 'prod'", notice);
        Assert.DoesNotContain(Typed, notice);
    }

    [Fact]
    public async Task The_next_session_reads_it_from_the_keychain_and_does_not_ask()
    {
        var store = new FakeSecretStore();
        var info = Conn();
        await RefusedThenRetriedAsync(NewRig(store, Typed), info);

        var nextSession = NewRig(store);   // a fresh resolver: nothing in memory, no answers queued
        await nextSession.Manager.GetOrConnectAsync(info, CancellationToken.None);

        Assert.Equal(0, nextSession.Prompt.Calls);
        Assert.Equal(Typed, nextSession.Provider.LastPassword);
    }

    [Fact]
    public async Task A_password_whose_connect_fails_is_never_saved()
    {
        var store = new FakeSecretStore();
        var rig = NewRig(store, "mistyped");
        var info = Conn();

        await RefusedThenRetriedAsync(rig, info, retrySucceeds: false);

        Assert.Equal(1, rig.Prompt.Calls);
        Assert.Null(await store.GetPasswordAsync(info.Id, CancellationToken.None));
        Assert.Empty(rig.Notices);
    }

    [Fact]
    public async Task A_retry_that_works_replaces_a_stale_saved_password()
    {
        var store = new FakeSecretStore();
        var info = Conn();
        store.Seed(info.Id, "old-password");
        var rig = NewRig(store, "new-password");

        await RefusedThenRetriedAsync(rig, info);

        Assert.Equal("new-password", rig.Provider.LastPassword);   // the stale one was not sent again
        Assert.Equal("new-password", await store.GetPasswordAsync(info.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_domain_account_asks_at_once_and_saves_once_it_works()
    {
        var store = new FakeSecretStore();
        var rig = NewRig(store, Typed);
        var info = Conn(CredentialKind.DomainPassword);

        await rig.Manager.GetOrConnectAsync(info, CancellationToken.None);

        Assert.Equal(1, rig.Prompt.Calls);
        Assert.Equal(Typed, await store.GetPasswordAsync(info.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_ask_every_time_kind_is_never_saved()
    {
        var store = new FakeSecretStore();
        var rig = NewRig(store, Typed);
        var info = Conn(CredentialKind.Prompt);

        await rig.Manager.GetOrConnectAsync(info, CancellationToken.None);

        Assert.Equal(Typed, rig.Provider.LastPassword);
        Assert.Null(await store.GetPasswordAsync(info.Id, CancellationToken.None));
        Assert.Empty(rig.Notices);
    }

    [Fact]
    public async Task With_no_keyring_it_stays_in_memory_and_connects_without_a_save_attempt()
    {
        var store = new FakeSecretStore { CanStore = false };
        var rig = NewRig(store, Typed);
        var info = Conn();

        await RefusedThenRetriedAsync(rig, info);

        Assert.Equal(Typed, rig.Provider.LastPassword);
        Assert.Null(await store.GetPasswordAsync(info.Id, CancellationToken.None));
        Assert.Empty(rig.Notices);   // nothing was attempted; the posture is already in the status bar (§1.1)
        var again = await rig.Resolver.ResolveAsync(info, forceRefresh: false, CancellationToken.None);
        Assert.Equal(Typed, again.Secret);
        Assert.Equal(1, rig.Prompt.Calls);
    }

    [Fact]
    public async Task A_keychain_that_refuses_keeps_it_in_memory_connects_and_says_why_once()
    {
        var store = new RefusingSecretStore { UnavailableReason = "no D-Bus session" };
        var rig = NewRig(store, Typed);
        var info = Conn();

        await RefusedThenRetriedAsync(rig, info);

        var notice = Assert.Single(rig.Notices);
        Assert.Contains("keychain couldn't be reached (no D-Bus session)", notice);
        Assert.Contains("kept until Bearing closes", notice);
        Assert.DoesNotContain(Typed, notice);
        // Connected, and the session keeps serving it without asking again.
        Assert.Equal(Typed, (await rig.Resolver.ResolveAsync(info, forceRefresh: false, CancellationToken.None)).Secret);
        Assert.Equal(1, rig.Prompt.Calls);
        // A second database on the same connection does not try (or say it) again.
        await rig.Manager.GetOrConnectAsync(info with { Database = "reporting" }, CancellationToken.None);
        Assert.Single(rig.Notices);
    }

    [Fact]
    public async Task A_keychain_that_errors_is_reported_not_thrown()
    {
        var store = new RefusingSecretStore { Throws = new InvalidOperationException("keyring locked") };
        var rig = NewRig(store, Typed);
        var info = Conn();

        await RefusedThenRetriedAsync(rig, info);

        var notice = Assert.Single(rig.Notices);
        Assert.Contains("could not be saved to the keychain: keyring locked", notice);
    }

    [Fact]
    public void Without_a_prompt_a_stored_password_is_not_retried_as_before()
    {
        // The CLI: nobody to ask, so a retry would send the same stored password and fail identically.
        var resolver = new CredentialResolver(() => new FakeSecretStore(), null, new FakeEntraTokens(_ => new Credential(null, null)));

        Assert.False(resolver.CanRefresh(Conn()));
    }

    /// <summary>A store that says it can keep secrets and then refuses (or fails) the write.</summary>
    private sealed class RefusingSecretStore : ISecretStore
    {
        public bool IsSecure => true;
        public bool CanStore => true;
        public string? UnavailableReason { get; init; }
        public Exception? Throws { get; init; }
        public Task<string?> GetPasswordAsync(Guid id, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task SetPasswordAsync(Guid id, string password, CancellationToken ct)
            => throw (Throws ?? new SecretStorageRefusedException("refused (fake)"));
    }
}
