using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Accept-new's second half (§1.12): the first host key a tunnel sees is written into the saved connection by
/// <see cref="ConnectionsViewModel.PinHostKeyAsync"/>, and a pin that could not be written is said — an unpinned
/// connection treats every later key as first sight, so silence would turn accept-new into accept-always.
/// </summary>
public class SshHostKeyPinTests : IDisposable
{
    private const string Seen = "SHA256:seen";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-sshpin", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static ConnectionInfo Conn(string? pin = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "behind-bastion",
        ProviderId = "postgres",
        Host = "db",
        User = "u",
        SshTunnel = new SshTunnel { Host = "bastion", Port = 22, User = "jump", HostKeyFingerprint = pin },
    };

    private (WorkspaceContext Ctx, ConnectionsViewModel Conns, Func<string> Status) Compose(
        IProjectStore? store = null, params ConnectionInfo[] saved)
    {
        Directory.CreateDirectory(_root);
        var ctx = new WorkspaceContext(
            new FakeProvider(),
            store ?? new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Name = "p" } };
        ctx.Project.Manifest.Connections.AddRange(saved);
        var status = "";
        ctx.Status = s => status = s;
        return (ctx, new ConnectionsViewModel(ctx), () => status);
    }

    private static string? PinOf(WorkspaceContext ctx, Guid id)
        => ctx.Project!.Manifest.Connections.Single(c => c.Id == id).SshTunnel!.HostKeyFingerprint;

    [Fact]
    public async Task A_first_seen_key_is_pinned_and_saved()
    {
        var conn = Conn();
        var (ctx, conns, status) = Compose(saved: conn);

        await conns.PinHostKeyAsync(conn, Seen);

        Assert.Equal(Seen, PinOf(ctx, conn.Id));
        var reopened = await new JsonProjectStore().OpenAsync(_root, CancellationToken.None);
        Assert.Equal(Seen, reopened.Manifest.Connections.Single(c => c.Id == conn.Id).SshTunnel!.HostKeyFingerprint);
        Assert.StartsWith("Trusted the SSH host key", status());
    }

    [Fact]
    public async Task An_existing_pin_is_never_overwritten()
    {
        var conn = Conn("SHA256:earlier");
        var (ctx, conns, status) = Compose(saved: conn);

        await conns.PinHostKeyAsync(conn with { SshTunnel = conn.SshTunnel! with { HostKeyFingerprint = null } }, Seen);

        Assert.Equal("SHA256:earlier", PinOf(ctx, conn.Id));
        Assert.Equal("", status());
    }

    [Fact]
    public async Task A_key_seen_on_a_route_edited_since_is_not_pinned()
    {
        var conn = Conn();
        var (ctx, conns, status) = Compose(saved: conn);

        // The connect used the old bastion; the record now points at another one.
        await conns.PinHostKeyAsync(conn with { SshTunnel = conn.SshTunnel! with { Host = "old-bastion" } }, Seen);

        Assert.Null(PinOf(ctx, conn.Id));
        Assert.Equal("", status());
    }

    [Fact]
    public async Task A_connection_gone_from_the_project_is_reported_unpinned()
    {
        var (_, conns, status) = Compose();

        await conns.PinHostKeyAsync(Conn(), Seen);

        Assert.StartsWith("Could not pin the SSH host key of bastion", status());
        Assert.Contains("not trusted yet", status());
    }

    [Fact]
    public async Task A_pin_the_project_could_not_save_is_reported_unpinned()
    {
        var conn = Conn();
        var (_, conns, status) = Compose(new FailingSaveStore(), conn);

        await conns.PinHostKeyAsync(conn, Seen);

        Assert.StartsWith("Could not pin the SSH host key of bastion", status());
        Assert.Contains("could not be saved", status());
    }

    [Fact]
    public async Task A_prompt_connection_keeps_its_ssh_secret_out_of_the_keychain()
    {
        var conn = Conn() with { CredentialKind = CredentialKind.Prompt };
        var (ctx, conns, _) = Compose(saved: conn);
        var secrets = (FakeSecretStore)ctx.Secrets!;
        secrets.Seed(SshSecretId.For(conn.Id), "saved-before-the-switch");

        await conns.AddOrUpdateConnectionAsync(conn, password: "", sshSecret: "typed");

        Assert.Null(await secrets.GetPasswordAsync(SshSecretId.For(conn.Id), CancellationToken.None));
    }

    /// <summary>A project store whose save fails, as a full disk or a read-only folder would.</summary>
    private sealed class FailingSaveStore : IProjectStore
    {
        public Task<Project> CreateAsync(string directory, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<Project> OpenAsync(string directory, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(Project project, CancellationToken ct) => throw new IOException("disk full");
        public Task DeleteAsync(string directory, CancellationToken ct) => throw new NotSupportedException();
    }
}
