using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
/// Override with <c>BEARING_TEST_SSH_HOST/PORT/USER/PASSWORD/DB_HOST</c>, and <c>BEARING_TEST_SSH_CONTAINER</c> for
/// the bastion container the dropped-tunnel test reaches into.
/// </summary>
public class LiveSshTunnelTests
{
    private static string SshHost => Environment.GetEnvironmentVariable("BEARING_TEST_SSH_HOST") ?? "127.0.0.1";
    private static int SshPort => int.TryParse(Environment.GetEnvironmentVariable("BEARING_TEST_SSH_PORT"), out var p) ? p : 2223;
    private static string SshUser => Environment.GetEnvironmentVariable("BEARING_TEST_SSH_USER") ?? "bearing";
    private static string SshPassword => Environment.GetEnvironmentVariable("BEARING_TEST_SSH_PASSWORD") ?? "sshpass";
    private static string Container => Environment.GetEnvironmentVariable("BEARING_TEST_SSH_CONTAINER") ?? "bearing-ssh-test";
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

        await using var tunnel = await credentials.Ssh.OpenAsync(info, info.Host, info.Port, CancellationToken.None);

        Assert.Equal(1, prompt.Calls);
        Assert.True(tunnel.LocalPort > 0);
        Assert.Equal(SshPassword, await secrets.GetPasswordAsync(SshSecretId.For(info.Id), CancellationToken.None));
    }
    [SkippableFact]
    public async Task A_stale_unpinned_copy_is_held_to_the_pin_saved_since()
    {
        // An explorer node keeps the record it was built from — here, from before the first key was pinned. The
        // tunnel must check the server against the pin saved now, not read the old copy as "first sight".
        await RequireAsync();
        var stale = Info();
        var (sessions, credentials, _) = Compose(stale);
        credentials.Ssh.HostKeyPinned = (_, _) => Task.CompletedTask;
        credentials.Ssh.SavedTunnel = id => id == stale.Id
            ? stale.SshTunnel! with { HostKeyFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" }
            : null;

        await using (sessions)
        {
            var ex = await Assert.ThrowsAsync<ConnectionFailedException>(
                () => sessions.GetOrConnectAsync(stale, CancellationToken.None));
            Assert.Contains("CHANGED", ex.Message);
        }
    }

    [SkippableFact]
    public async Task A_stale_pinned_copy_gives_way_to_a_pin_forgotten_since()
    {
        // The other way round: the key rotated, the user pressed Forget host key and saved, and an explorer node
        // still holds the record with the old pin. The saved record (no pin) wins, so the new key is accepted
        // and pinned rather than refused with "use Forget host key and save" — which the user just did.
        await RequireAsync();
        var stale = Info("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var (sessions, credentials, _) = Compose(stale);
        var pinned = new List<string>();
        credentials.Ssh.HostKeyPinned = (_, fp) => { pinned.Add(fp); return Task.CompletedTask; };
        credentials.Ssh.SavedTunnel = id => id == stale.Id ? stale.SshTunnel! with { HostKeyFingerprint = null } : null;

        await using (sessions)
        {
            var session = await sessions.GetOrConnectAsync(stale, CancellationToken.None);
            Assert.NotNull(session.Tunnel);
        }
        Assert.StartsWith("SHA256:", Assert.Single(pinned));
    }

    [SkippableFact]
    public async Task Every_database_of_a_connection_shares_one_ssh_session_and_one_prompt()
    {
        // A tab connecting while the tree loads, or several databases: concurrent first opens queue behind one
        // login, so the password is asked for once and the bastion sees one SSH session, not one per pool.
        await RequireAsync();
        var info = Info();
        var secrets = new FakeSecretStore();
        secrets.Seed(info.Id, MySqlTestServer.Password);
        var prompt = new FakeCredentialPrompt(SshPassword);
        var credentials = new CredentialResolver(() => secrets, prompt, new ThrowingEntraTokens());
        credentials.Ssh.HostKeyPinned = (_, _) => Task.CompletedTask;

        var tunnels = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => credentials.Ssh.OpenAsync(info, info.Host, info.Port, CancellationToken.None)));
        try
        {
            Assert.Equal(1, prompt.Calls);
            Assert.Equal(1, credentials.Ssh.OpenSessionCount);
            Assert.Equal(4, tunnels.Select(t => t.LocalPort).Distinct().Count());
            Assert.All(tunnels, t => Assert.True(t.IsAlive));
        }
        finally
        {
            foreach (var tunnel in tunnels)
            {
                await tunnel.DisposeAsync();
            }
        }
        // The last forward released disconnects the session.
        Assert.Equal(0, credentials.Ssh.OpenSessionCount);
    }

    [SkippableFact]
    public async Task A_dropped_tunnel_is_evicted_and_the_next_acquire_opens_a_new_one()
    {
        await RequireAsync();
        var info = Info();
        var (sessions, credentials, _) = Compose(info);
        credentials.Ssh.HostKeyPinned = (_, _) => Task.CompletedTask;
        var key = SessionKey.For(info);

        await using (sessions)
        {
            var first = await sessions.GetOrConnectAsync(info, CancellationToken.None);
            var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sessions.LiveChanged += k =>
            {
                if (k == key && sessions.TryGet(key) is null)
                {
                    evicted.TrySetResult();
                }
            };

            // The bastion ends this client's SSH session (sleep/wake or a bastion restart, as seen from here);
            // its listener stays up, as a real bastion's would.
            await KillSshSessionsAsync();

            await evicted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(sessions.IsLinked(info.Id));   // the indicator says the server is not reachable

            var second = await sessions.GetOrConnectAsync(info, CancellationToken.None);
            Assert.NotSame(first, second);
            var result = await second.Executor.ExecuteAsync("select 1 + 1 as two", new QueryOptions(), CancellationToken.None);
            Assert.Null(result[0].Error);
            Assert.Equal(2L, Convert.ToInt64(result[0].Rows[0][0]));
        }
    }

    /// <summary>Kill the bastion's per-client <c>sshd-session</c> processes, never its listener (which runs as
    /// the same user in this image). Skips when the container cannot be reached with <c>docker exec</c>.</summary>
    private static async Task KillSshSessionsAsync()
    {
        var psi = new ProcessStartInfo("docker", ["exec", Container, "pkill", "-f", "sshd-session"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process? process = null;
        try { process = Process.Start(psi); }
        catch (Exception) { Skip.If(true, "docker is not available to drop the SSH session"); }
        using (process)
        {
            await process!.WaitForExitAsync();
            // pkill: 0 = killed something, 1 = matched nothing (a test bug), anything else = no container.
            Skip.If(process.ExitCode > 1, $"docker exec {Container} failed — run ./build/test-ssh.sh");
            Assert.True(process.ExitCode == 0, "No sshd-session process to kill: the tunnel's session was not found.");
        }
    }
}
