using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Finding a relation in the schema tree (#117) — the matching that "go to table" and F12 both end in.
/// <para>
/// Real node view models over a fake browser rather than the live tree: the nodes' own lazy loading is the
/// thing under test (a relation cannot be matched until its database has been expanded), and it is awaitable
/// without a window.
/// </para>
/// </summary>
public class SchemaTreeRevealTests : IDisposable
{
    public void Dispose()
    {
        foreach (var dir in _cleanup)
            try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
    }

    private static readonly Guid ConnId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static ConnectionInfo Conn(Guid? id = null, string name = "srv") => new()
    {
        Id = id ?? ConnId,
        Name = name,
        ProviderId = "postgres",
        Host = "h",
        Port = 5432,
        Database = "app",
        User = "u",
    };

    private static async Task<ServerNodeViewModel> Server(ISchemaBrowser browser, ConnectionInfo? info = null)
    {
        var server = new ServerNodeViewModel(info ?? Conn(), browser);
        await server.EnsureChildrenAsync();
        return server;
    }

    // ---- locating the server row ----------------------------------------------------------------------

    [Fact]
    public async Task A_server_at_the_root_is_found_by_its_connection_id()
    {
        var server = await Server(new RevealBrowser());
        var found = SchemaTreeReveal.ServerFor([server], ConnId);
        Assert.Same(server, found);
    }

    [Fact]
    public async Task A_server_inside_a_folder_is_found_without_expanding_anything()
    {
        // Folders are built eagerly with their children (#80), which is what lets a reveal reach a
        // connection the user has filed away and never opened.
        var server = await Server(new RevealBrowser());
        var folder = new ConnectionFolderNodeViewModel("Production", 1, [server]);

        Assert.False(folder.IsExpanded);
        Assert.Same(server, SchemaTreeReveal.ServerFor([folder], ConnId));
    }

    [Fact]
    public async Task An_unknown_connection_is_not_found()
    {
        var server = await Server(new RevealBrowser());
        Assert.Null(SchemaTreeReveal.ServerFor([server], Guid.NewGuid()));
    }

    // ---- locating the database and the relation -------------------------------------------------------

    [Fact]
    public async Task A_database_is_matched_by_name()
    {
        var server = await Server(new RevealBrowser());

        Assert.NotNull(SchemaTreeReveal.DatabaseUnder(server, "app"));
        Assert.NotNull(SchemaTreeReveal.DatabaseUnder(server, "reporting"));
        Assert.Null(SchemaTreeReveal.DatabaseUnder(server, "nope"));
    }

    [Fact]
    public async Task An_exact_database_name_wins_over_a_case_insensitive_one()
    {
        // Two databases on one server can differ only in case, and revealing a table in the wrong one would
        // be silent.
        var server = await Server(new RevealBrowser { Databases = ["App", "app"] });

        var exact = SchemaTreeReveal.DatabaseUnder(server, "app");
        Assert.Equal("app", exact!.Title);
    }

    [Fact]
    public async Task A_relation_is_matched_on_its_schema_and_name_not_its_label()
    {
        // The row for a relation in the default schema is labelled `film`, and one elsewhere
        // `reporting.summary` — so matching titles would find the first and miss the second.
        var server = await Server(new RevealBrowser());
        var database = SchemaTreeReveal.DatabaseUnder(server, "app")!;
        await database.EnsureChildrenAsync();

        var inDefault = SchemaTreeReveal.RelationUnder(database, "public", "film");
        Assert.NotNull(inDefault);
        Assert.Equal("film", inDefault.Title);

        var elsewhere = SchemaTreeReveal.RelationUnder(database, "reporting", "summary");
        Assert.NotNull(elsewhere);
        Assert.Equal("reporting.summary", elsewhere.Title);
    }

    [Fact]
    public async Task A_view_is_found_inside_its_collapsed_bucket()
    {
        // Views sit behind a collapsed group row, and reaching into one costs nothing: buckets arrive
        // populated from the snapshot.
        var server = await Server(new RevealBrowser());
        var database = SchemaTreeReveal.DatabaseUnder(server, "app")!;
        await database.EnsureChildrenAsync();

        var bucket = database.Children.OfType<SchemaGroupNodeViewModel>().Single(g => g.Title == "Views");
        Assert.False(bucket.IsExpanded);

        Assert.NotNull(SchemaTreeReveal.RelationUnder(database, "public", "film_list"));
    }

    [Fact]
    public async Task A_relation_the_database_does_not_have_is_not_found()
    {
        var server = await Server(new RevealBrowser());
        var database = SchemaTreeReveal.DatabaseUnder(server, "app")!;
        await database.EnsureChildrenAsync();

        Assert.Null(SchemaTreeReveal.RelationUnder(database, "public", "nope"));
        Assert.Null(SchemaTreeReveal.RelationUnder(database, "other_schema", "film"));
    }

    [Fact]
    public async Task A_column_is_found_under_an_expanded_relation()
    {
        var server = await Server(new RevealBrowser());
        var database = SchemaTreeReveal.DatabaseUnder(server, "app")!;
        await database.EnsureChildrenAsync();
        var relation = SchemaTreeReveal.RelationUnder(database, "public", "film")!;
        await relation.EnsureChildrenAsync();

        Assert.NotNull(SchemaTreeReveal.ColumnUnder(relation, "title"));
        Assert.Null(SchemaTreeReveal.ColumnUnder(relation, "no_such_column"));
    }

    [Fact]
    public async Task Relations_under_a_database_include_the_bucketed_ones_and_nothing_else()
    {
        var server = await Server(new RevealBrowser());
        var database = SchemaTreeReveal.DatabaseUnder(server, "app")!;
        await database.EnsureChildrenAsync();

        var names = SchemaTreeReveal.RelationsUnder(database).Select(r => r.RelationName).ToList();

        Assert.Contains("film", names);          // inline
        Assert.Contains("film_list", names);     // inside the Views bucket
        Assert.Contains("summary", names);       // another schema
        Assert.DoesNotContain("calc", names);    // a routine is not a relation
    }

    [Fact]
    public void The_target_reads_as_the_object_it_names()
    {
        Assert.Equal("public.film", new SchemaTreeReveal.Target(ConnId, "app", "public", "film").Label);
        Assert.Equal("public.film.title",
            new SchemaTreeReveal.Target(ConnId, "app", "public", "film", "title").Label);
    }

    // ---- the descent, through a real view model -------------------------------------------------------

    /// <summary>
    /// A <see cref="ConnectionsViewModel"/> over a fake browser, so the reveal's awaited descent — expand,
    /// load, match, repeat — is testable without a server. The browser is injected into the context (§2.4)
    /// rather than reached for, which is what makes this possible at all.
    /// </summary>
    private (WorkspaceContext Ctx, ConnectionsViewModel Vm, ConnectionInfo Conn) NewVm(
        ISchemaBrowser? browser = null)
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "bearing-reveal", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        _cleanup.Add(root);

        var ctx = new WorkspaceContext(
            new FakeProvider(),
            new Bearing.Persistence.JsonProjectStore(),
            new Bearing.Persistence.JsonSessionStore(),
            new Bearing.Persistence.SqliteQueryLog(System.IO.Path.Combine(root, "log.sqlite")),
            new Bearing.Persistence.FileRecentProjects(System.IO.Path.Combine(root, "recent.json")),
            new FakeSecretStore(),
            schema: browser ?? new RevealBrowser());

        var conn = Conn();
        var manifest = new Bearing.Core.Workspace.ProjectManifest();
        manifest.Connections.Add(conn);
        ctx.Project = new Bearing.Core.Workspace.Project { Directory = root, Manifest = manifest };

        var vm = new ConnectionsViewModel(ctx);
        vm.RefreshConnections();
        return (ctx, vm, conn);
    }

    private readonly List<string> _cleanup = [];

    [Fact]
    public async Task Revealing_a_relation_expands_down_to_it_and_selects_it()
    {
        var (_, vm, conn) = NewVm();

        var result = await vm.RevealRelationAsync(
            new SchemaTreeReveal.Target(conn.Id, "app", "public", "film"));

        Assert.Equal(SchemaRevealResult.Revealed, result);
        var selected = Assert.IsType<RelationNodeViewModel>(vm.SelectedSchemaNode);
        Assert.Equal("film", selected.RelationName);

        // Everything above it is open, or the selection would be invisible — which is the whole job.
        var server = SchemaTreeReveal.ServerFor(vm.ServerNodes, conn.Id)!;
        Assert.True(server.IsExpanded);
        Assert.True(SchemaTreeReveal.DatabaseUnder(server, "app")!.IsExpanded);
    }

    [Fact]
    public async Task Revealing_a_column_expands_the_relation_too()
    {
        var (_, vm, conn) = NewVm();

        var result = await vm.RevealRelationAsync(
            new SchemaTreeReveal.Target(conn.Id, "app", "public", "film", "title"));

        Assert.Equal(SchemaRevealResult.Revealed, result);
        Assert.Equal("title", vm.SelectedSchemaNode?.Title);
    }

    [Fact]
    public async Task A_column_the_relation_does_not_have_still_lands_on_the_relation()
    {
        // The closest true answer. Selecting nothing would leave the user staring at the tree they asked to
        // be taken into.
        var (_, vm, conn) = NewVm();

        var result = await vm.RevealRelationAsync(
            new SchemaTreeReveal.Target(conn.Id, "app", "public", "film", "no_such_column"));

        Assert.Equal(SchemaRevealResult.NoColumn, result);
        Assert.Equal("film", Assert.IsType<RelationNodeViewModel>(vm.SelectedSchemaNode).RelationName);
    }

    /// <summary>
    /// Each failure is its own outcome, because each is a different sentence for the status bar — a bool
    /// would collapse "that connection is gone" and "the schema has changed" into one useless message.
    /// </summary>
    [Fact]
    public async Task Each_way_it_can_fail_reports_itself()
    {
        var (_, vm, conn) = NewVm();

        Assert.Equal(SchemaRevealResult.NoConnection, await vm.RevealRelationAsync(
            new SchemaTreeReveal.Target(Guid.NewGuid(), "app", "public", "film")));
        Assert.Equal(SchemaRevealResult.NoDatabase, await vm.RevealRelationAsync(
            new SchemaTreeReveal.Target(conn.Id, "nope", "public", "film")));
        Assert.Equal(SchemaRevealResult.NoRelation, await vm.RevealRelationAsync(
            new SchemaTreeReveal.Target(conn.Id, "app", "public", "nope")));
    }

    [Fact]
    public async Task A_reveal_in_simple_mode_leaves_the_schema_folders_shut()
    {
        // The descent into a schema exists for full mode, where the relation is three levels down and the
        // schema is lazy. Simple mode has the row inline *and* builds a Schemas level, so descending
        // unconditionally opened a folder nobody asked for and built a second set of nodes for relations
        // already on screen — every F12, every time.
        var (_, vm, conn) = NewVm();

        var result = await vm.RevealRelationAsync(
            new SchemaTreeReveal.Target(conn.Id, "app", "public", "film"));

        Assert.Equal(SchemaRevealResult.Revealed, result);

        var server = SchemaTreeReveal.ServerFor(vm.ServerNodes, conn.Id)!;
        var database = SchemaTreeReveal.DatabaseUnder(server, "app")!;
        var folders = SchemaTreeReveal.SchemaFoldersUnder(database).ToList();

        // The level is there — this is a two-schema database, so the group was built.
        Assert.NotEmpty(folders);
        Assert.All(folders, f => Assert.False(f.IsExpanded));
        // And none of them was made to load: an unopened folder still holds only its placeholder.
        Assert.All(folders, f => Assert.IsType<MessageNodeViewModel>(f.Children.Single()));
    }

    [Fact]
    public async Task A_filter_hiding_the_row_is_cleared_rather_than_failing_the_reveal()
    {
        // A search typed earlier is not a reason for a reveal the user just asked for to find nothing.
        var (_, vm, conn) = NewVm();
        vm.ConnectionFilter = "no-such-connection";

        var result = await vm.RevealRelationAsync(
            new SchemaTreeReveal.Target(conn.Id, "app", "public", "film"));

        Assert.Equal(SchemaRevealResult.Revealed, result);
        Assert.Equal("", vm.ConnectionFilter);
    }

    // ---- the fake --------------------------------------------------------------------------------------

    /// <summary>
    /// Two databases, and relations in two schemas plus a view and a routine — the shapes a reveal has to
    /// tell apart. Deterministic, like the demo fixtures (§4.6).
    /// </summary>
    private sealed class RevealBrowser : ISchemaBrowser
    {
        public IReadOnlyList<string> Databases { get; init; } = ["app", "reporting"];

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult(Databases);

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
        {
            var tables = new[]
            {
                new TableInfo(1, "public", "film", RelationKind.Table),
                new TableInfo(2, "public", "film_list", RelationKind.View),
                new TableInfo(3, "reporting", "summary", RelationKind.Table),
            };
            var columns = new[]
            {
                new ColumnInfo(1, 1, "film_id", "integer", NotNull: true, IsPrimaryKey: true),
                new ColumnInfo(1, 2, "title", "text", NotNull: false, IsPrimaryKey: false),
            };
            var snapshot = new SchemaSnapshot(
                database, ["public", "reporting"], tables, columns, [], searchPath: ["public"]);
            var routines = new[] { new RoutineInfo(10, "public", "calc", RoutineKind.Function, "a integer", "integer") };
            return Task.FromResult(new DatabaseObjects(snapshot, routines));
        }

        public Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult(TableDetails.Empty);

        public Task<string> GetViewDefinitionAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult("select 1");

        public Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RelationSize>>([]);

        public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DatabaseSize>>([]);

        /// <summary>#119's per-database kinds. Empty here: these fixtures exist for other questions, and a
        /// group they never assert on would only add noise to the trees they build.</summary>
        public Task<DatabaseObjectKinds> GetDatabaseObjectKindsAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(DatabaseObjectKinds.Empty);

        /// <summary>#120's roles. Empty here: these fixtures exist for other questions, and a Roles group
        /// they never assert on would only add noise to the trees they build.</summary>
        public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RoleInfo>>([]);

        public Task<RoleGrants> GetRoleGrantsAsync(
            ConnectionInfo connection, string database, string roleName, CancellationToken ct)
            => Task.FromResult(RoleGrants.Of([]));

        /// <summary>Tablespaces (#119 follow-up). Empty here for the same reason the roles are.</summary>
        public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(
            ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SchemaObjectInfo>>([]);

        public Task<string> GetRoutineDefinitionAsync(
            ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("CREATE FUNCTION calc() ...");

        public Task InvalidateAsync(Guid connectionId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
