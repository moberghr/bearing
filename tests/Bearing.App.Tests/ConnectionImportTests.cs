using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Applying a parsed import to the project (#72). The parsing half lives in
/// <c>Bearing.Persistence.Tests/DBeaverImportTests</c>; this covers what happens when it meets a project
/// that may already hold some of the same servers — which is the normal case, because people import, spot a
/// gap, and import again.
/// </summary>
public class ConnectionImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-connimport", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static ConnectionInfo Conn(string name, string host = "db.example", int port = 5432,
                                       string database = "app", string user = "karlo", string? folder = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = "postgres",
            Host = host,
            Port = port,
            Database = database,
            User = user,
            Folder = folder,
        };

    private (WorkspaceContext ctx, ConnectionsViewModel vm, FakeSecretStore secrets) NewVm(
        params ConnectionInfo[] connections)
    {
        Directory.CreateDirectory(_root);
        var secrets = new FakeSecretStore();
        var ctx = new WorkspaceContext(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            secrets);
        var manifest = new ProjectManifest();
        manifest.Connections.AddRange(connections);
        ctx.Project = new Project { Directory = _root, Manifest = manifest };
        var vm = new ConnectionsViewModel(ctx);
        vm.RefreshConnections();
        return (ctx, vm, secrets);
    }

    [Fact]
    public async Task Importing_into_an_empty_project_adds_everything()
    {
        var (ctx, vm, _) = NewVm();

        var outcome = await vm.ImportConnectionsAsync(new[] { Conn("a"), Conn("b", port: 5433) });

        Assert.Equal(2, outcome.Added);
        Assert.Equal(0, outcome.Updated);
        Assert.Equal(2, ctx.Project!.Manifest.Connections.Count);
    }

    [Fact]
    public async Task Folders_are_declared_so_the_grouping_arrives_with_the_connections()
    {
        var (ctx, vm, _) = NewVm();

        await vm.ImportConnectionsAsync(new[] { Conn("a", folder: "Aur/Production") }, new[] { "Aur/Production" });

        Assert.Contains("Aur/Production", ctx.Project!.Manifest.ConnectionFolders);
        var aur = vm.ServerNodes.OfType<ConnectionFolderNodeViewModel>().Single(f => f.Title == "Aur");
        Assert.Equal(1, aur.Count);
    }

    [Fact]
    public async Task Re_importing_updates_the_match_rather_than_appending_a_duplicate()
    {
        var existing = Conn("prod");
        var (ctx, vm, _) = NewVm(existing);

        // Same server, different name and folder — matched on where it points, never on what it is called.
        var outcome = await vm.ImportConnectionsAsync(new[] { Conn("Prod Aur", folder: "Aur") });

        Assert.Equal(0, outcome.Added);
        Assert.Equal(1, outcome.Updated);
        var only = Assert.Single(ctx.Project!.Manifest.Connections);
        Assert.Equal("Prod Aur", only.Name);
        Assert.Equal("Aur", only.Folder);
    }

    [Fact]
    public async Task An_updated_match_keeps_its_id_so_its_saved_password_still_works()
    {
        var existing = Conn("prod");
        var (ctx, vm, secrets) = NewVm(existing);
        await secrets.SetPasswordAsync(existing.Id, "hunter2", CancellationToken.None);

        await vm.ImportConnectionsAsync(new[] { Conn("Prod Aur") });

        // The id is the secret-store key. Replacing it would strand the password the user already saved.
        var only = ctx.Project!.Manifest.Connections.Single();
        Assert.Equal(existing.Id, only.Id);
        Assert.Equal("hunter2", await secrets.GetPasswordAsync(only.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_updated_match_keeps_its_credential_kind()
    {
        var existing = Conn("prod") with { CredentialKind = CredentialKind.EntraToken };
        var (ctx, vm, _) = NewVm(existing);

        // The importer marks every row Prompt (it can carry no password); that must not downgrade a
        // connection the user has already configured for Entra.
        await vm.ImportConnectionsAsync(new[] { Conn("prod") with { CredentialKind = CredentialKind.Prompt } });

        Assert.Equal(CredentialKind.EntraToken, ctx.Project!.Manifest.Connections.Single().CredentialKind);
    }

    [Fact]
    public async Task Declining_updates_leaves_matches_untouched_and_still_adds_the_new_ones()
    {
        var existing = Conn("prod");
        var (ctx, vm, _) = NewVm(existing);

        var outcome = await vm.ImportConnectionsAsync(
            new[] { Conn("Prod Aur"), Conn("Staging Aur", port: 5433) }, updateExisting: false);

        Assert.Equal(1, outcome.Added);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(2, ctx.Project!.Manifest.Connections.Count);
        Assert.Contains(ctx.Project.Manifest.Connections, c => c.Name == "prod");   // untouched
    }

    [Fact]
    public async Task A_different_port_on_the_same_host_is_a_different_server()
    {
        var (ctx, vm, _) = NewVm(Conn("local", host: "localhost", port: 5432));

        var outcome = await vm.ImportConnectionsAsync(new[] { Conn("test", host: "localhost", port: 5434) });

        Assert.Equal(1, outcome.Added);
        Assert.Equal(2, ctx.Project!.Manifest.Connections.Count);
    }

    [Fact]
    public async Task An_imported_name_that_clashes_is_suffixed_rather_than_shadowing()
    {
        // Same name, different server — both have to remain findable.
        var (ctx, vm, _) = NewVm(Conn("prod", host: "one.example"));

        await vm.ImportConnectionsAsync(new[] { Conn("prod", host: "two.example") });

        Assert.Equal(new[] { "prod", "prod (copy)" },
            ctx.Project!.Manifest.Connections.Select(c => c.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Importing_nothing_writes_nothing()
    {
        var (ctx, vm, _) = NewVm(Conn("prod"));

        var outcome = await vm.ImportConnectionsAsync(Array.Empty<ConnectionInfo>());

        Assert.Equal(0, outcome.Added);
        Assert.Single(ctx.Project!.Manifest.Connections);
    }

    [Fact]
    public async Task The_import_survives_a_round_trip_to_disk()
    {
        var (_, vm, _) = NewVm();

        await vm.ImportConnectionsAsync(new[] { Conn("a", folder: "Aur") }, new[] { "Aur" });

        var reloaded = await new JsonProjectStore().OpenAsync(_root, default);
        Assert.Equal("Aur", reloaded.Manifest.Connections.Single().Folder);
        Assert.Contains("Aur", reloaded.Manifest.ConnectionFolders);
    }

    [Fact]
    public async Task The_first_import_into_an_empty_project_gives_new_tabs_something_to_target()
    {
        var (ctx, vm, _) = NewVm();

        await vm.ImportConnectionsAsync(new[] { Conn("a") });

        Assert.NotNull(ctx.DefaultConnectionId);
    }
}
