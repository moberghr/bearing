using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Sessions;
using Bearing.Testing;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The user's scenario end to end against the real PostgreSQL and the real Npgsql: a stored-password connection
/// just imported onto a machine whose keychain holds nothing for it. The first login goes out with no password
/// and is refused client-side by Npgsql; that refusal must read as an authentication failure, so the user's
/// connect asks once, connects, and saves the password. The fake-server tests could not see this — their
/// server refused both cases the same way (see LoginClassificationLiveTests in Bearing.Data.Tests).
/// </summary>
public class LivePromptedPasswordTests
{
    private sealed record Rig(
        CredentialResolver Resolver, ConnectionSessionManager Sessions, SchemaBrowser Browser,
        FakeCredentialPrompt Prompt, FakeSecretStore Store, List<string> Notices);

    private static Rig NewRig()
    {
        var registry = new ProviderRegistry();
        var store = new FakeSecretStore();   // empty: nothing saved for the imported connection
        var prompt = new FakeCredentialPrompt(PgTestServer.Password);
        var resolver = new CredentialResolver(() => store, prompt, new FakeEntraTokens(_ => new Credential(null, null)));
        var notices = new List<string>();
        resolver.PasswordSaveNotice = (_, text) => { lock (notices) notices.Add(text); };
        return new Rig(resolver, new ConnectionSessionManager(registry, () => resolver, runSweepTimer: false),
            new SchemaBrowser(registry, () => resolver), prompt, store, notices);
    }

    private static ConnectionInfo Imported()
        => PgTestServer.Info("imported") with { CredentialKind = CredentialKind.StoredPassword };

    [SkippableFact]
    public async Task Connect_on_a_fresh_import_asks_once_connects_and_saves_the_password()
    {
        await PgTestServer.RequireAsync();
        var rig = NewRig();
        await using var _ = rig.Sessions;
        var info = Imported();

        var session = await rig.Sessions.ConnectForUserAsync(info, CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(1, rig.Prompt.Calls);
        Assert.Equal(PgTestServer.Password, await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
        Assert.Contains(rig.Notices, n => n.Contains("Saved the password for 'imported'"));
    }

    [SkippableFact]
    public async Task Expanding_the_server_row_on_a_fresh_import_asks_once_and_saves_the_password()
    {
        await PgTestServer.RequireAsync();
        var rig = NewRig();
        await using var _ = rig.Browser;
        var info = Imported();

        var databases = await rig.Browser.GetDatabasesForUserAsync(info, CancellationToken.None);

        Assert.Contains(PgTestServer.Database, databases);
        Assert.Equal(1, rig.Prompt.Calls);
        Assert.Equal(PgTestServer.Password, await rig.Store.GetPasswordAsync(info.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_background_connect_on_a_fresh_import_fails_without_asking()
    {
        await PgTestServer.RequireAsync();
        var rig = NewRig();
        await using var _ = rig.Sessions;

        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => rig.Sessions.GetOrConnectAsync(Imported(), CancellationToken.None));

        Assert.Equal(0, rig.Prompt.Calls);
    }
}
