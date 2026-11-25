using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
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
/// "Move to project…" on a server row: the connection leaves the project on screen for another one on this
/// machine, and must still connect there without asking for anything — while the source settles what it
/// held on it the way a delete does (§1.10).
/// </summary>
public class ConnectionMoveToProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-move", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private const string Password = "db-password-1";
    private const string SshSecret = "ssh-secret-1";

    private sealed record Harness(
        WorkspaceContext Ctx, ConnectionsViewModel Vm, FakeSecretStore Secrets, ConnectionInfo Conn,
        Project Source, string TargetDir, JsonProjectStore Store);

    private async Task<Harness> NewHarness(
        Func<ConnectionInfo, ProjectManifest>? targetManifest = null, IProjectStore? store = null)
    {
        Directory.CreateDirectory(_root);
        var json = new JsonProjectStore();
        var secrets = new FakeSecretStore();
        var ctx = new WorkspaceContext(
            new FakeProvider(),
            store ?? json,
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            secrets,
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));

        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "prod",
            ProviderId = "postgres",
            Host = "db.example",
            Database = "app",
            User = "karlo",
            Folder = "Clients/Acme",
            ManualCommit = true,
            ReadOnly = true,
            ExternalAccess = ExternalAccess.ReadOnly,
            CredentialKind = CredentialKind.StoredPassword,
            SshTunnel = new SshTunnel { Host = "bastion", User = "jump", HostKeyFingerprint = "SHA256:pin" },
        };
        secrets.Seed(conn.Id, Password);
        secrets.Seed(SshSecretId.For(conn.Id), SshSecret);

        var source = await json.CreateAsync(Path.Combine(_root, "source"), "Source", CancellationToken.None);
        source.Manifest.Connections.Add(conn);
        await json.SaveAsync(source, CancellationToken.None);
        ctx.Project = source;
        ctx.GetOrAdd(source);

        var targetDir = Path.Combine(_root, "target");
        var target = await json.CreateAsync(targetDir, "Target", CancellationToken.None);
        if (targetManifest is not null)
        {
            target.Manifest = targetManifest(conn);
            await json.SaveAsync(target, CancellationToken.None);
        }

        var vm = new ConnectionsViewModel(ctx);
        vm.RefreshConnections();
        return new Harness(ctx, vm, secrets, conn, source, targetDir, json);
    }

    private static Task<Project> OnDisk(Harness h, string dir) => h.Store.OpenAsync(dir, CancellationToken.None);

    [Fact]
    public async Task The_connection_moves_with_its_id_settings_and_folder()
    {
        var h = await NewHarness();

        Assert.True(await h.Vm.MoveConnectionToProjectAsync(h.Conn.Id, h.TargetDir));

        var landed = Assert.Single((await OnDisk(h, h.TargetDir)).Manifest.Connections);
        Assert.Equal(h.Conn.Id, landed.Id);   // kept: the secret key and the query log's key
        Assert.Equal("prod", landed.Name);
        Assert.Equal("Clients/Acme", landed.Folder);
        Assert.True(landed.ReadOnly);
        Assert.Equal("SHA256:pin", landed.SshTunnel!.HostKeyFingerprint);
        // Same user, same machine: the exposure decision was already made, and is kept.
        Assert.Equal(ExternalAccess.ReadOnly, landed.ExternalAccess);
        Assert.Contains("Clients/Acme", (await OnDisk(h, h.TargetDir)).Manifest.ConnectionFolders);

        Assert.Empty((await OnDisk(h, h.Source.Directory)).Manifest.Connections);
        Assert.Empty(h.Source.Manifest.Connections);
        Assert.Empty(h.Vm.ServerNodes.OfType<ServerNodeViewModel>());
    }

    [Fact]
    public async Task Moving_keeps_the_saved_password_and_ssh_secret_under_the_kept_id()
    {
        var h = await NewHarness();

        await h.Vm.MoveConnectionToProjectAsync(h.Conn.Id, h.TargetDir);

        // A delete would have removed these; a move must not — the moved connection is still keyed by them.
        Assert.Equal(Password, await h.Secrets.GetPasswordAsync(h.Conn.Id, CancellationToken.None));
        Assert.Equal(SshSecret, await h.Secrets.GetPasswordAsync(SshSecretId.For(h.Conn.Id), CancellationToken.None));
    }

    [Fact]
    public async Task A_target_already_holding_the_id_gets_a_fresh_one_with_both_secrets_carried()
    {
        // A project.json copied by hand: the target has a connection with this very id.
        var h = await NewHarness(conn => new ProjectManifest
        {
            Name = "Target",
            Connections = { conn with { Host = "elsewhere" } },
        });

        Assert.True(await h.Vm.MoveConnectionToProjectAsync(h.Conn.Id, h.TargetDir));

        var target = (await OnDisk(h, h.TargetDir)).Manifest.Connections;
        Assert.Equal(2, target.Count);
        var moved = target.Single(c => c.Host == "db.example");
        Assert.NotEqual(h.Conn.Id, moved.Id);
        Assert.Equal("prod (copy)", moved.Name);   // the name was taken too
        Assert.Equal(Password, await h.Secrets.GetPasswordAsync(moved.Id, CancellationToken.None));
        Assert.Equal(SshSecret, await h.Secrets.GetPasswordAsync(SshSecretId.For(moved.Id), CancellationToken.None));
        // The target's own connection still uses the old id's entries, so they stay.
        Assert.Equal(Password, await h.Secrets.GetPasswordAsync(h.Conn.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_name_taken_in_the_target_gets_the_import_suffix_and_keeps_the_id()
    {
        var h = await NewHarness(_ => new ProjectManifest
        {
            Name = "Target",
            Connections = { new ConnectionInfo { Id = Guid.NewGuid(), Name = "PROD", ProviderId = "postgres", Host = "other" } },
        });
        var status = "";
        h.Ctx.Status = s => status = s;

        await h.Vm.MoveConnectionToProjectAsync(h.Conn.Id, h.TargetDir);

        var moved = (await OnDisk(h, h.TargetDir)).Manifest.Connections.Single(c => c.Host == "db.example");
        Assert.Equal("prod (copy)", moved.Name);
        Assert.Equal(h.Conn.Id, moved.Id);
        Assert.Contains("Named 'prod (copy)' there", status);
    }

    [Fact]
    public async Task An_open_target_project_is_changed_in_memory_not_behind_its_back()
    {
        var h = await NewHarness();
        var parked = await h.Store.OpenAsync(h.TargetDir, CancellationToken.None);
        h.Ctx.GetOrAdd(parked);   // opened this session: its manifest is the live one

        await h.Vm.MoveConnectionToProjectAsync(h.Conn.Id, h.TargetDir);

        Assert.Equal(h.Conn.Id, Assert.Single(parked.Manifest.Connections).Id);
    }

    [Fact]
    public async Task An_open_transaction_on_a_source_tab_is_rolled_back_and_reported()
    {
        var h = await NewHarness();
        // Writable and untunnelled: read-only refuses the write, and the fake provider has no SSH route to open.
        h.Source.Manifest.Connections[0] = h.Conn with { SshTunnel = null, ReadOnly = false };
        var exec = new ExecutionViewModel(h.Ctx, null);
        var tab = new EditorTabViewModel("t") { ConnectionId = h.Conn.Id };
        h.Ctx.Tabs.Add(tab);
        h.Ctx.SelectedTab = tab;
        await exec.ExecuteAsync("insert into t values (1)");
        var scope = Assert.IsType<FakeTransactionScope>(h.Ctx.Transactions.For(tab)!.Scope);
        var status = "";
        h.Ctx.Status = s => status = s;

        Assert.True(await h.Vm.MoveConnectionToProjectAsync(h.Conn.Id, h.TargetDir));

        Assert.Equal(1, scope.Rollbacks);
        Assert.Equal(0, h.Ctx.Transactions.Count);
        Assert.Null(tab.ConnectionId);   // the source tab no longer has that connection to point at
        Assert.Contains("Rolled back 1 uncommitted transaction", status);
    }

    [Fact]
    public async Task A_running_query_on_it_refuses_the_move()
    {
        var gated = new GatedExecutor();
        var h = await NewHarness();
        var tab = new EditorTabViewModel("t") { ConnectionId = h.Conn.Id };
        h.Ctx.Tabs.Add(tab);
        h.Ctx.SelectedTab = tab;
        ((FakeProvider)h.Ctx.Providers).Executor = gated;
        var running = new ExecutionViewModel(h.Ctx, null).ExecuteAsync("select 1");
        var deadline = Environment.TickCount64 + 5000;
        while (!tab.IsRunning) { Assert.True(Environment.TickCount64 < deadline); await Task.Delay(10); }
        var status = "";
        h.Ctx.Status = s => status = s;

        Assert.False(await h.Vm.MoveConnectionToProjectAsync(h.Conn.Id, h.TargetDir));

        Assert.Contains("still running", status);
        Assert.Single(h.Source.Manifest.Connections);
        Assert.Empty((await OnDisk(h, h.TargetDir)).Manifest.Connections);
        gated.Release("select 1");
        await running;
    }

    [Fact]
    public async Task A_target_that_cannot_be_saved_leaves_the_source_untouched()
    {
        var failing = new FailingSaveStore(new JsonProjectStore());
        var h = await NewHarness(store: failing);
        failing.FailFor = Path.GetFullPath(h.TargetDir);
        var status = "";
        h.Ctx.Status = s => status = s;

        Assert.False(await h.Vm.MoveConnectionToProjectAsync(h.Conn.Id, h.TargetDir));

        Assert.Contains("could not be saved", status);
        Assert.Contains("still here", status);
        Assert.Single(h.Source.Manifest.Connections);
        Assert.Single((await OnDisk(h, h.Source.Directory)).Manifest.Connections);
        Assert.Equal(Password, await h.Secrets.GetPasswordAsync(h.Conn.Id, CancellationToken.None));
    }

    [Fact]
    public void Planning_is_pure_and_keeps_the_id_when_free()
    {
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "a", ProviderId = "postgres", Folder = " x / y " };

        var plan = ConnectionProjectMove.PlanMove(conn, new ProjectManifest());

        Assert.Equal(conn.Id, plan.Landed.Id);
        Assert.False(plan.IdReminted);
        Assert.False(plan.Renamed);
        Assert.Equal("x/y", plan.Landed.Folder);
    }

    /// <summary>A project store whose save fails for one directory — a target that can't be written.</summary>
    private sealed class FailingSaveStore(IProjectStore inner) : IProjectStore
    {
        public string? FailFor { get; set; }
        public Task<Project> CreateAsync(string directory, string name, CancellationToken ct) => inner.CreateAsync(directory, name, ct);
        public Task<Project> OpenAsync(string directory, CancellationToken ct) => inner.OpenAsync(directory, ct);
        public Task DeleteAsync(string directory, CancellationToken ct) => inner.DeleteAsync(directory, ct);
        public Task SaveAsync(Project project, CancellationToken ct)
            => string.Equals(Path.GetFullPath(project.Directory), FailFor, StringComparison.Ordinal)
                ? throw new IOException("disk full (fake)")
                : inner.SaveAsync(project, ct);
    }
}
