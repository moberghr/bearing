using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Sessions;
using Bearing.Testing;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The tunnel end to end: Bearing → SSH bastion (<c>build/test-ssh.sh</c>) → MariaDB, which the bastion reaches
/// by container name. Skipped when the bastion or MariaDB is not up — and a skip is not a pass (§4.2).
/// Override with <c>BEARING_TEST_SSH_HOST/PORT/USER/PASSWORD/DB_HOST</c>.
/// </summary>
public class LiveSshTunnelTests
{
    private static string SshHost => Environment.GetEnvironmentVariable("BEARING_TEST_SSH_HOST") ?? "127.0.0.1";
    private static int SshPort => int.TryParse(Environment.GetEnvironmentVariable("BEARING_TEST_SSH_PORT"), out var p) ? p : 2223;
    private static string SshUser => Environment.GetEnvironmentVariable("BEARING_TEST_SSH_USER") ?? "bearing";
    private static string SshPassword => Environment.GetEnvironmentVariable("BEARING_TEST_SSH_PASSWORD") ?? "sshpass";
    private static string DbHostFromBastion => Environment.GetEnvironmentVariable("BEARING_TEST_SSH_DB_HOST") ?? "bearing-mariadb-test";

    private static async Task RequireAsync()
    {
        await MySqlTestServer.RequireAsync(MySqlEngine.MariaDb);
        using var probe = new TcpClient();
        try { await probe.ConnectAsync(SshHost, SshPort).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { Skip.If(true, $"No SSH bastion at {SshHost}:{SshPort} — run ./build/test-ssh.sh"); }
    }

    private static ConnectionInfo Info(string? pin = null) => MySqlTestServer.Info(MySqlEngine.MariaDb, "via-ssh") with
    {
        Host = DbHostFromBastion,
        Port = 3306,
        SshTunnel = new SshTunnel { Host = SshHost, Port = SshPort, User = SshUser, HostKeyFingerprint = pin },
    };

    private static (ConnectionSessionManager Sessions, CredentialResolver Credentials, FakeSecretStore Secrets) Compose(ConnectionInfo info)
    {
        var secrets = new FakeSecretStore();
        secrets.Seed(info.Id, MySqlTestServer.Password);
        secrets.Seed(SshSecretId.For(info.Id), SshPassword);
        var credentials = new CredentialResolver(() => secrets, prompt: null, new ThrowingEntraTokens());
        var sessions = new ConnectionSessionManager(new ProviderRegistry(), () => credentials, runSweepTimer: false);
        return (sessions, credentials, secrets);
    }

    [SkippableFact]
    public async Task A_query_runs_through_the_tunnel_and_the_first_host_key_is_pinned()
    {
        await RequireAsync();
        var info = Info();
        var (sessions, credentials, _) = Compose(info);
        var pinned = new List<string>();
        credentials.Ssh.HostKeyPinned = (_, fp) => { pinned.Add(fp); return Task.CompletedTask; };

        await using (sessions)
        {
            var session = await sessions.GetOrConnectAsync(info, CancellationToken.None);
            var result = await session.Executor.ExecuteAsync("select 1 + 1 as two", new QueryOptions(), CancellationToken.None);
            Assert.Null(result[0].Error);
            Assert.Equal(2L, Convert.ToInt64(result[0].Rows[0][0]));
            Assert.NotNull(session.Tunnel);
        }

        var fp = Assert.Single(pinned);
        Assert.StartsWith("SHA256:", fp);

        // Pinned and matching: connects, and nothing is pinned again.
        var again = Info(fp) with { Id = info.Id };
        var (sessions2, credentials2, _) = Compose(again);
        credentials2.Ssh.HostKeyPinned = (_, f) => { pinned.Add(f); return Task.CompletedTask; };
        await using (sessions2)
        {
            await sessions2.GetOrConnectAsync(again, CancellationToken.None);
        }
        Assert.Single(pinned);
    }

    [SkippableFact]
    public async Task A_changed_host_key_is_refused()
    {
        await RequireAsync();
        var info = Info("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var (sessions, _, _) = Compose(info);
        await using (sessions)
        {
            var ex = await Assert.ThrowsAsync<ConnectionFailedException>(
                () => sessions.GetOrConnectAsync(info, CancellationToken.None));
            Assert.Contains("CHANGED", ex.Message);
        }
    }

    [SkippableFact]
    public async Task An_unpinned_key_is_refused_where_nothing_can_pin_it()
    {
        await RequireAsync();
        var info = Info();
        var (sessions, _, _) = Compose(info);   // no HostKeyPinned: the CLI's posture
        await using (sessions)
        {
            var ex = await Assert.ThrowsAsync<ConnectionFailedException>(
                () => sessions.GetOrConnectAsync(info, CancellationToken.None));
            Assert.Contains("not trusted yet", ex.Message);
        }
    }

    [SkippableFact]
    public async Task A_refused_ssh_password_is_asked_for_once_and_kept_when_it_works()
    {
        await RequireAsync();
        var info = Info();
        var secrets = new FakeSecretStore();
        secrets.Seed(info.Id, MySqlTestServer.Password);
        secrets.Seed(SshSecretId.For(info.Id), "wrong");
        var prompt = new FakeCredentialPrompt(SshPassword);
        var credentials = new CredentialResolver(() => secrets, prompt, new ThrowingEntraTokens());
        credentials.Ssh.HostKeyPinned = (_, _) => Task.CompletedTask;

        await using var tunnel = await credentials.Ssh.OpenAsync(info, CancellationToken.None);

        Assert.Equal(1, prompt.Calls);
        Assert.True(tunnel.LocalPort > 0);
        Assert.Equal(SshPassword, await secrets.GetPasswordAsync(SshSecretId.For(info.Id), CancellationToken.None));
    }
}
