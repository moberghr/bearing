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
/// The one retry every <b>user-initiated</b> connect gets after a refused login (<see cref="AuthRetry"/>): the
/// Connect button (<see cref="IConnectionSessionManager.ConnectForUserAsync"/>) and expanding a server or
/// database row (<see cref="ISchemaBrowser.GetDatabasesForUserAsync"/> / <c>GetObjectsForUserAsync</c>) ask for
/// the password and save it once it worked — while background work (the plain methods) never asks, and two
/// failures at once end in one prompt.
/// </summary>
public class UserConnectRetryTests
{
    private const string Right = "the-right-password";

    private static ConnectionInfo Conn() => new()
    {
        Id = Guid.NewGuid(),
        Name = "imported",
        ProviderId = "postgres",
        Host = "db",
        Database = "app",
        User = "karlo",
        CredentialKind = CredentialKind.StoredPassword,
    };

    private sealed record Rig(
        FakeProvider Provider, CredentialResolver Resolver, ConnectionSessionManager Sessions, SchemaBrowser Browser,
        ICredentialPrompt Prompt, FakeSecretStore Store, List<string> Notices);

    /// <summary>A server that accepts only <see cref="Right"/>, a keychain with nothing saved for the
    /// connection (it was just imported), and a prompt.</summary>
    private static Rig NewRig(ICredentialPrompt prompt)
    {
        var provider = new FakeProvider { AcceptsPassword = p => p == Right };
        var store = new FakeSecretStore();
        var resolver = new CredentialResolver(() => store, prompt, new FakeEntraTokens(_ => new Credential(null, null)));
        var notices = new List<string>();
        resolver.PasswordSaveNotice = (_, text) => { lock (notices) notices.Add(text); };
        return new Rig(provider, resolver, new ConnectionSessionManager(provider, () => resolver, runSweepTimer: false),
            new SchemaBrowser(provider, () => resolver), prompt, store, notices);
    }

    private static int Calls(Rig rig) => rig.Prompt switch
    {
        FakeCredentialPrompt f => f.Calls,
        GatedPrompt g => g.Calls,
        _ => throw new InvalidOperationException(),
    };

    // ---- the Connect button ------------------------------------------------------------------------

    [Fact]
    public async Task Connect_with_no_saved_password_asks_once_and_saves_it()
    {
        var rig = NewRig(new FakeCredentialPrompt(Right));
        var info = Conn();

        var session = await rig.Sessions.ConnectForUserAsync(info, CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(1, Calls(rig));
        Assert.Equal(Right, await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
        Assert.Contains(rig.Notices, n => n.Contains("Saved the password for 'imported'"));
    }

    [Fact]
    public async Task A_failure_that_is_not_a_refused_login_is_not_retried()
    {
        var rig = NewRig(new FakeCredentialPrompt(Right));
        rig.Provider.AcceptsPassword = null;
        rig.Provider.TestResult = false;   // unreachable, not refused

        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => rig.Sessions.ConnectForUserAsync(Conn(), CancellationToken.None));

        Assert.Equal(0, Calls(rig));
    }

    [Fact]
    public async Task A_wrong_password_on_the_retry_fails_and_is_not_saved()
    {
        var rig = NewRig(new FakeCredentialPrompt("wrong"));
        var info = Conn();

        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => rig.Sessions.ConnectForUserAsync(info, CancellationToken.None));

        Assert.Equal(1, Calls(rig));   // one retry, not a loop
        Assert.Null(await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
    }

    // ---- the sidebar tree --------------------------------------------------------------------------

    [Fact]
    public async Task Expanding_a_server_row_asks_once_and_saves_it_after_the_catalog_answered()
    {
        var rig = NewRig(new FakeCredentialPrompt(Right));
        var info = Conn();

        var databases = await rig.Browser.GetDatabasesForUserAsync(info, CancellationToken.None);

        Assert.Equal(["app"], databases);
        Assert.Equal(1, Calls(rig));
        Assert.Equal(Right, await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Expanding_a_database_row_asks_once_and_saves_it()
    {
        var rig = NewRig(new FakeCredentialPrompt(Right));
        var info = Conn();

        var objects = await rig.Browser.GetObjectsForUserAsync(info, "app", CancellationToken.None);

        Assert.NotNull(objects.Snapshot);
        Assert.Equal(Right, await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_wrong_password_at_the_tree_is_not_saved()
    {
        var rig = NewRig(new FakeCredentialPrompt("wrong"));
        var info = Conn();

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Browser.GetDatabasesForUserAsync(info, CancellationToken.None));

        Assert.Null(await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
        Assert.Empty(rig.Notices);
    }

    // ---- background work never asks -----------------------------------------------------------------

    [Fact]
    public async Task A_background_connect_that_is_refused_never_prompts()
    {
        // Schema warm-up (§9.4) and every other background caller use GetOrConnectAsync.
        var rig = NewRig(new FakeCredentialPrompt(Right));
        var info = Conn();

        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => rig.Sessions.GetOrConnectAsync(info, CancellationToken.None));

        Assert.Equal(0, Calls(rig));
        Assert.Null(await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_background_tree_read_that_is_refused_never_prompts()
    {
        var rig = NewRig(new FakeCredentialPrompt(Right));

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Browser.GetDatabasesAsync(Conn(), CancellationToken.None));

        Assert.Equal(0, Calls(rig));
    }

    // ---- one prompt per failure ---------------------------------------------------------------------

    [Fact]
    public async Task Two_user_connects_refused_at_once_end_in_one_prompt()
    {
        var prompt = new GatedPrompt();
        var rig = NewRig(prompt);
        var info = Conn();

        // A tree expand and the Connect button, racing on the same just-imported connection.
        var expand = rig.Browser.GetDatabasesForUserAsync(info, CancellationToken.None);
        var connect = rig.Sessions.ConnectForUserAsync(info, CancellationToken.None);
        await prompt.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);   // give the other caller every chance to ask a second time
        prompt.Answer(Right);

        await Task.WhenAll(expand, connect).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, prompt.Calls);
        Assert.Equal(Right, await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
        Assert.Single(rig.Notices, n => n.Contains("Saved the password"));
    }

    [Fact]
    public void A_refresh_someone_else_already_made_is_not_made_again()
    {
        var resolver = new CredentialResolver(() => new FakeSecretStore(), new FakeCredentialPrompt(),
            new FakeEntraTokens(_ => new Credential(null, null)));
        var id = Guid.NewGuid();
        var mine = resolver.Generation(id);

        resolver.Invalidate(id);   // another caller's failure got there first

        Assert.False(resolver.InvalidateIfUnchanged(id, mine));
        Assert.True(resolver.InvalidateIfUnchanged(id, resolver.Generation(id)));
    }

    /// <summary>A prompt that holds its answer until the test gives it, so concurrent callers are in flight
    /// together while the user is "typing".</summary>
    private sealed class GatedPrompt : ICredentialPrompt
    {
        private readonly TaskCompletionSource<string?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Answer(string password) => _answer.TrySetResult(password);

        public Task<string?> RequestPasswordAsync(ConnectionInfo info, string? message, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Asked.TrySetResult();
            return _answer.Task.WaitAsync(ct);
        }
    }
}
