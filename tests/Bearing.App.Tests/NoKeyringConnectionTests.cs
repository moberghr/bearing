using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// What happens on a machine with no keyring, where the secret store refuses to write
/// (<see cref="ISecretStore.CanStore"/> false). The connection must still save, the user must be told the
/// password wasn't kept, and connecting must fall back to prompting instead of failing forever against a
/// password that was never stored.
/// </summary>
public class NoKeyringConnectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-nokeyring", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static ConnectionInfo Conn(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "prod",
        ProviderId = "postgres",
        Host = "h",
        User = "u",
        CredentialKind = CredentialKind.StoredPassword,
    };

    private (WorkspaceContext ctx, ConnectionsViewModel conns) NewContext(ISecretStore secrets)
    {
        Directory.CreateDirectory(_root);
        var ctx = new WorkspaceContext(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            secrets,
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Name = "p" } };
        return (ctx, new ConnectionsViewModel(ctx));
    }

    [Fact]
    public async Task Saving_a_connection_keeps_it_and_reports_that_the_password_was_not_stored()
    {
        var secrets = new FakeSecretStore { IsSecure = false, CanStore = false };
        var (ctx, conns) = NewContext(secrets);
        var status = "";
        ctx.Status = s => status = s;
        var conn = Conn();

        await conns.AddOrUpdateConnectionAsync(conn, "typed-password");

        // The connection itself is saved — refusing the secret is not a failed save.
        Assert.Contains(ctx.Project!.Manifest.Connections, c => c.Id == conn.Id);
        var reopened = await new JsonProjectStore().OpenAsync(_root, CancellationToken.None);
        Assert.Contains(reopened.Manifest.Connections, c => c.Id == conn.Id);

        // …and the user is told what happened to the password, in those words.
        Assert.Contains("password not saved", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("asked", status, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await secrets.GetPasswordAsync(conn.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_working_keyring_still_stores_the_password_silently()
    {
        var secrets = new FakeSecretStore(); // secure + can store
        var (ctx, conns) = NewContext(secrets);
        var status = "";
        ctx.Status = s => status = s;
        var conn = Conn();

        await conns.AddOrUpdateConnectionAsync(conn, "typed-password");

        Assert.Equal("typed-password", await secrets.GetPasswordAsync(conn.Id, CancellationToken.None));
        Assert.DoesNotContain("not saved", status);
    }

    [Fact]
    public async Task A_stored_password_connection_with_nothing_stored_connects_passwordless_first_then_prompts()
    {
        var secrets = new FakeSecretStore { IsSecure = false, CanStore = false };
        var prompt = new FakeCredentialPrompt("typed-at-connect");
        var resolver = new CredentialResolver(() => secrets, prompt, new ThrowingEntraTokens());
        var info = Conn();

        // First attempt: no password and no prompt, so a passwordless server (trust auth, .pgpass) still
        // connects without the user being interrogated.
        var first = await resolver.ResolveAsync(info, forceRefresh: false, CancellationToken.None);
        Assert.Null(first.Secret);
        Assert.Equal(0, prompt.Calls);

        // The retry after an authentication failure is what asks — and the answer is reused for the session,
        // never written to the store.
        var retried = await resolver.ResolveAsync(info, forceRefresh: true, CancellationToken.None);
        Assert.Equal("typed-at-connect", retried.Secret);
        Assert.Equal(1, prompt.Calls);

        var again = await resolver.ResolveAsync(info, forceRefresh: false, CancellationToken.None);
        Assert.Equal("typed-at-connect", again.Secret);
        Assert.Equal(1, prompt.Calls);                       // served from memory
        Assert.Null(await secrets.GetPasswordAsync(info.Id, CancellationToken.None)); // and never persisted
    }

    [Fact]
    public async Task A_secret_left_over_from_before_is_still_used_and_never_prompts()
    {
        var secrets = new FakeSecretStore { IsSecure = false, CanStore = false };
        var info = Conn();
        secrets.Seed(info.Id, "written-before-the-opt-out");
        var prompt = new FakeCredentialPrompt("should-not-be-asked");
        var resolver = new CredentialResolver(() => secrets, prompt, new ThrowingEntraTokens());

        var cred = await resolver.ResolveAsync(info, forceRefresh: false, CancellationToken.None);

        Assert.Equal("written-before-the-opt-out", cred.Secret);
        Assert.Equal(0, prompt.Calls);
    }
}
