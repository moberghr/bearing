using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Folder operations on the connections panel (#80): create, rename, move, delete, and filing a connection.
/// Driven through <see cref="ConnectionsViewModel"/> over a real <see cref="JsonProjectStore"/>, because the
/// invariant that matters most is that the manifest and the tree never disagree — a folder that is on screen
/// but not on disk is worse than no folders at all.
/// </summary>
public class ConnectionFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-connfolder", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static ConnectionInfo Conn(string name, string? folder = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = "postgres",
            Host = "localhost",
            Port = 5432,
            Database = "app",
            Folder = folder,
        };

    private (WorkspaceContext ctx, ConnectionsViewModel vm) NewVm(params ConnectionInfo[] connections)
    {
        Directory.CreateDirectory(_root);
        var ctx = new WorkspaceContext(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore());
        var manifest = new ProjectManifest();
        manifest.Connections.AddRange(connections);
        ctx.Project = new Project { Directory = _root, Manifest = manifest };
        var vm = new ConnectionsViewModel(ctx);
        vm.RefreshConnections();
        return (ctx, vm);
    }

    private static ConnectionFolderNodeViewModel Folder(ConnectionsViewModel vm, string name)
        => vm.ServerNodes.OfType<ConnectionFolderNodeViewModel>().Single(f => f.Title == name);

    private static ConnectionFolderNodeViewModel? FindFolder(ConnectionsViewModel vm, string name)
        => vm.ServerNodes.OfType<ConnectionFolderNodeViewModel>().FirstOrDefault(f => f.Title == name);

    private static string? FolderOf(WorkspaceContext ctx, string connectionName)
        => ctx.Project!.Manifest.Connections.Single(c => c.Name == connectionName).Folder;

    // ---- create --------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_folder_shows_it_and_persists_it_empty()
    {
        var (ctx, vm) = NewVm();

        await vm.CreateFolderAsync("Aur Production");

        Assert.Equal("Aur Production", Folder(vm, "Aur Production").Title);
        Assert.Contains("Aur Production", ctx.Project!.Manifest.ConnectionFolders);

        // The whole reason ConnectionFolders exists: an empty folder has to survive a round trip.
        var reloaded = await new JsonProjectStore().OpenAsync(_root, default);
        Assert.Contains("Aur Production", reloaded.Manifest.ConnectionFolders);
    }

    [Fact]
    public async Task Creating_a_folder_twice_is_a_no_op_rather_than_a_duplicate()
    {
        var (ctx, vm) = NewVm();

        await vm.CreateFolderAsync("Aur");
        await vm.CreateFolderAsync("Aur");

        Assert.Single(ctx.Project!.Manifest.ConnectionFolders);
    }

    [Fact]
    public async Task A_typed_name_cannot_smuggle_in_a_nesting_level()
    {
        var (ctx, vm) = NewVm();

        await vm.CreateFolderAsync("Aur/Production");

        Assert.Equal(new[] { "Aur-Production" }, ctx.Project!.Manifest.ConnectionFolders);
    }

    [Fact]
    public async Task A_subfolder_nests_under_its_parent()
    {
        var (_, vm) = NewVm();

        await vm.CreateFolderAsync("Aur");
        await vm.CreateFolderAsync("Production", "Aur");

        var aur = Folder(vm, "Aur");
        var production = aur.Children.OfType<ConnectionFolderNodeViewModel>().Single();
        Assert.Equal("Aur/Production", production.Path);
    }

    // ---- filing a connection -------------------------------------------------------------------

    [Fact]
    public async Task Moving_a_connection_files_it_and_persists()
    {
        var conn = Conn("prod");
        var (ctx, vm) = NewVm(conn);
        await vm.CreateFolderAsync("Aur");

        await vm.MoveConnectionToFolderAsync(conn.Id, "Aur");

        Assert.Equal("Aur", FolderOf(ctx, "prod"));
        Assert.Empty(vm.ServerNodes.OfType<ServerNodeViewModel>());   // no longer at the root
        Assert.Single(Folder(vm, "Aur").Children.OfType<ServerNodeViewModel>());
    }

    [Fact]
    public async Task Filing_a_connection_keeps_its_node_and_everything_it_loaded()
    {
        var conn = Conn("prod");
        var (_, vm) = NewVm(conn);
        await vm.CreateFolderAsync("Aur");
        var before = vm.ServerNodes.OfType<ServerNodeViewModel>().Single();
        before.IsExpanded = true;

        await vm.MoveConnectionToFolderAsync(conn.Id, "Aur");

        // Filing is cosmetic: SameNetwork ignores the folder, so the pool, the schema and the loaded
        // databases all stay put.
        var after = Folder(vm, "Aur").Children.OfType<ServerNodeViewModel>().Single();
        Assert.Same(before, after);
        Assert.True(after.IsExpanded);
    }

    [Fact]
    public async Task Moving_a_connection_to_the_root_clears_its_folder()
    {
        var conn = Conn("prod", "Aur");
        var (ctx, vm) = NewVm(conn);

        await vm.MoveConnectionToFolderAsync(conn.Id, null);

        Assert.Null(FolderOf(ctx, "prod"));
        Assert.Single(vm.ServerNodes.OfType<ServerNodeViewModel>());
    }

    // ---- rename --------------------------------------------------------------------------------

    [Fact]
    public async Task Renaming_a_folder_carries_its_connections_with_it()
    {
        var (ctx, vm) = NewVm(Conn("a", "Aur"), Conn("b", "Aur"));

        await vm.RenameFolderAsync("Aur", "Aurora");

        Assert.Equal("Aurora", FolderOf(ctx, "a"));
        Assert.Equal("Aurora", FolderOf(ctx, "b"));
        Assert.Equal(2, Folder(vm, "Aurora").Count);
    }

    [Fact]
    public async Task Renaming_a_folder_carries_its_whole_subtree()
    {
        var (ctx, vm) = NewVm(Conn("deep", "Aur/Production/Primary"));

        await vm.RenameFolderAsync("Aur", "Aurora");

        // Renaming the row without re-rooting what is under it would orphan the lot.
        Assert.Equal("Aurora/Production/Primary", FolderOf(ctx, "deep"));
    }

    [Fact]
    public async Task Renaming_a_nested_folder_leaves_its_parent_alone()
    {
        var (ctx, vm) = NewVm(Conn("x", "Aur/Production"), Conn("y", "Aur/Staging"));

        await vm.RenameFolderAsync("Aur/Production", "Prod");

        Assert.Equal("Aur/Prod", FolderOf(ctx, "x"));
        Assert.Equal("Aur/Staging", FolderOf(ctx, "y"));
    }

    [Fact]
    public async Task Renaming_to_blank_does_nothing()
    {
        var (ctx, vm) = NewVm(Conn("a", "Aur"));

        await vm.RenameFolderAsync("Aur", "   ");

        Assert.Equal("Aur", FolderOf(ctx, "a"));
    }

    // ---- move ----------------------------------------------------------------------------------

    [Fact]
    public async Task Moving_a_folder_re_roots_its_subtree()
    {
        var (ctx, vm) = NewVm(Conn("a", "Aur/Production"));
        await vm.CreateFolderAsync("Clients");

        await vm.MoveFolderAsync("Aur", "Clients");

        Assert.Equal("Clients/Aur/Production", FolderOf(ctx, "a"));
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_its_own_descendant()
    {
        var (ctx, vm) = NewVm(Conn("a", "Aur/Production"));

        await vm.MoveFolderAsync("Aur", "Aur/Production");

        // Allowing it would detach the subtree from the tree entirely.
        Assert.Equal("Aur/Production", FolderOf(ctx, "a"));
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_itself()
    {
        var (ctx, vm) = NewVm(Conn("a", "Aur"));

        await vm.MoveFolderAsync("Aur", "Aur");

        Assert.Equal("Aur", FolderOf(ctx, "a"));
    }

    [Fact]
    public async Task Moving_a_folder_to_the_root_lifts_its_subtree_out()
    {
        var (ctx, vm) = NewVm(Conn("a", "Clients/Aur/Production"));

        await vm.MoveFolderAsync("Clients/Aur", null);

        Assert.Equal("Aur/Production", FolderOf(ctx, "a"));
    }

    // ---- delete --------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_folder_keeps_its_connections()
    {
        var conn = Conn("prod", "Aur");
        var (ctx, vm) = NewVm(conn);

        await vm.DeleteFolderAsync("Aur");

        // Removing a container must never remove what was in it.
        Assert.Single(ctx.Project!.Manifest.Connections);
        Assert.Null(FolderOf(ctx, "prod"));
        Assert.Single(vm.ServerNodes.OfType<ServerNodeViewModel>());
        Assert.Null(FindFolder(vm, "Aur"));
    }

    [Fact]
    public async Task Deleting_a_folder_promotes_its_contents_one_level_not_to_the_root()
    {
        var (ctx, vm) = NewVm(Conn("a", "Clients/Aur/Production"));

        await vm.DeleteFolderAsync("Clients/Aur");

        Assert.Equal("Clients/Production", FolderOf(ctx, "a"));
    }

    [Fact]
    public async Task Deleting_a_folder_removes_it_from_the_declared_set()
    {
        var (ctx, vm) = NewVm();
        await vm.CreateFolderAsync("Aur");

        await vm.DeleteFolderAsync("Aur");

        Assert.Empty(ctx.Project!.Manifest.ConnectionFolders);
    }

    // ---- expansion -----------------------------------------------------------------------------

    [Fact]
    public async Task A_new_folder_starts_open()
    {
        var (_, vm) = NewVm(Conn("a", "Aur"));
        Assert.True(Folder(vm, "Aur").IsExpanded);
    }

    [Fact]
    public async Task Collapsing_a_folder_survives_a_rebuild()
    {
        var (_, vm) = NewVm(Conn("a", "Aur"), Conn("b"));
        Folder(vm, "Aur").IsExpanded = false;

        vm.ConnectionFilter = "";   // any edit or keystroke rebuilds the rows
        vm.RefreshConnections();

        Assert.False(Folder(vm, "Aur").IsExpanded);
    }

    [Fact]
    public async Task Collapsed_folders_ride_along_when_one_is_renamed()
    {
        var (_, vm) = NewVm(Conn("a", "Aur/Production"));
        var production = Folder(vm, "Aur").Children.OfType<ConnectionFolderNodeViewModel>().Single();
        production.IsExpanded = false;

        await vm.RenameFolderAsync("Aur", "Aurora");

        var moved = Folder(vm, "Aurora").Children.OfType<ConnectionFolderNodeViewModel>().Single();
        Assert.Equal("Aurora/Production", moved.Path);
        Assert.False(moved.IsExpanded);
    }

    [Fact]
    public void Restoring_a_session_normalises_the_paths_it_was_given()
    {
        var (_, vm) = NewVm(Conn("a", "Aur"));

        vm.RestoreCollapsedFolders(new[] { "  /Aur/  " });
        vm.RefreshConnections();

        // session.json is hand-editable; a stray slash must not resurrect a folder the user had closed.
        Assert.False(Folder(vm, "Aur").IsExpanded);
    }

    // ---- interaction with the filter -----------------------------------------------------------

    [Fact]
    public async Task Filtering_reaches_into_folders()
    {
        var (_, vm) = NewVm(Conn("netgiro", "Netgiro"), Conn("aur", "Aur"));

        vm.ConnectionFilter = "netgiro";

        Assert.Null(FindFolder(vm, "Aur"));
        Assert.Single(Folder(vm, "Netgiro").Children.OfType<ServerNodeViewModel>());
    }

    [Fact]
    public async Task Clearing_the_filter_restores_every_folder()
    {
        var (_, vm) = NewVm(Conn("netgiro", "Netgiro"), Conn("aur", "Aur"));

        vm.ConnectionFilter = "netgiro";
        vm.ConnectionFilter = "";

        Assert.NotNull(FindFolder(vm, "Aur"));
        Assert.NotNull(FindFolder(vm, "Netgiro"));
    }
}
