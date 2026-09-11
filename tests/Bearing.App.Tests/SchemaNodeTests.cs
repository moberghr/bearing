using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.App.Workspace;
using Xunit;

namespace Bearing.App.Tests;

public class SchemaNodeTests
{
    private static ConnectionInfo Conn()
        => new() { Id = Guid.NewGuid(), Name = "srv", ProviderId = "postgres", Host = "h", Port = 5432, Database = "app", User = "u" };

    [Fact]
    public async Task Server_node_loads_databases_once()
    {
        var browser = new FakeSchemaBrowser();
        var server = new ServerNodeViewModel(Conn(), browser);

        // Placeholder present before expansion so the expander shows.
        Assert.Single(server.Children);
        Assert.IsType<MessageNodeViewModel>(server.Children[0]);

        await server.EnsureChildrenAsync();
        await server.EnsureChildrenAsync(); // idempotent

        Assert.Equal(1, browser.DatabaseCalls);
        Assert.Equal(new[] { "app", "other" }, server.Children.Select(c => c.Title));
        Assert.All(server.Children, c => Assert.IsType<DatabaseNodeViewModel>(c));
        Assert.Equal("connected", server.Children.OfType<DatabaseNodeViewModel>().First(c => c.Title == "app").Detail);
    }

    /// <summary>Tables are the list; views and functions are one collapsed row each after them.</summary>
    [Fact]
    public async Task Database_node_lists_tables_then_buckets_the_rest()
    {
        var browser = new FakeSchemaBrowser();
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, browser);

        await db.EnsureChildrenAsync();

        Assert.Equal(new[] { "film", "zebra", "Views", "Functions" }, db.Children.Select(c => c.Title));
        Assert.IsType<RelationNodeViewModel>(db.Children[0]);

        var views = Assert.IsType<SchemaGroupNodeViewModel>(db.Children[2]);
        var functions = Assert.IsType<SchemaGroupNodeViewModel>(db.Children[3]);
        // Collapsed on arrival, but already populated — the members come from the loaded snapshot.
        Assert.False(views.IsExpanded);
        Assert.False(functions.IsExpanded);
        Assert.Equal(new[] { "film_list" }, views.Children.Select(c => c.Title));
        Assert.Equal(new[] { "calc" }, functions.Children.Select(c => c.Title));
        Assert.IsType<RoutineNodeViewModel>(functions.Children[0]);
        // The dim detail is the member count, so the bucket says how much it is hiding.
        Assert.Equal("1", views.Detail);
    }

    [Fact]
    public async Task An_empty_bucket_is_left_out_rather_than_shown_empty()
    {
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true,
            new MultiSchemaBrowser { ViewsAndRoutines = false });

        await db.EnsureChildrenAsync();

        Assert.DoesNotContain("Views", db.Children.Select(c => c.Title));
        Assert.DoesNotContain("Functions", db.Children.Select(c => c.Title));
        // The Schemas group is not a bucket of objects — it is a level over the same ones, and this fixture
        // has three schemas, so it is expected here (SchemaBreadthTests covers it).
        Assert.All(
            db.Children.Where(c => c.Title != "Schemas"),
            c => Assert.IsType<RelationNodeViewModel>(c));
    }

    /// <summary>The schema is on the row for anything outside the default schema, and those rows sort last.
    /// Without it, two same-named tables in different schemas were indistinguishable in the list.</summary>
    [Fact]
    public async Task Objects_outside_the_default_schema_are_prefixed_and_sort_below_it()
    {
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, new MultiSchemaBrowser());

        await db.EnsureChildrenAsync();

        Assert.Equal(
            new[] { "film", "audit.events", "audit.film", "billing.invoice", "Schemas", "Views", "Functions" },
            db.Children.Select(c => c.Title));
    }

    /// <summary>The schema appears once per row: in the detail for a bare title, in the title otherwise.</summary>
    [Fact]
    public async Task A_prefixed_row_does_not_repeat_its_schema_in_the_detail()
    {
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, new MultiSchemaBrowser());

        await db.EnsureChildrenAsync();

        Assert.Equal("table · public", db.Children.First(c => c.Title == "film").Detail);
        Assert.Equal("table", db.Children.First(c => c.Title == "audit.film").Detail);
        Assert.Equal("table", db.Children.First(c => c.Title == "billing.invoice").Detail);

        var views = db.Children.OfType<SchemaGroupNodeViewModel>().First(g => g.Title == "Views");
        Assert.Equal("view · public", views.Children.Single().Detail);
    }

    /// <summary>The default schema is search_path's head, not the literal "public".</summary>
    [Fact]
    public async Task The_default_schema_follows_search_path_not_the_name_public()
    {
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true,
            new MultiSchemaBrowser { SearchPath = new[] { "audit", "public" } });

        await db.EnsureChildrenAsync();

        // audit is now the bare, top-sorted schema; public gets the prefix.
        Assert.Equal(
            new[] { "events", "film", "billing.invoice", "public.film", "Schemas", "Views", "Functions" },
            db.Children.Select(c => c.Title));
        var views = db.Children.OfType<SchemaGroupNodeViewModel>().First(g => g.Title == "Views");
        Assert.Equal(new[] { "public.film_list" }, views.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Relation_node_expands_to_columns_from_snapshot()
    {
        var browser = new FakeSchemaBrowser();
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, browser);
        await db.EnsureChildrenAsync();
        var film = (RelationNodeViewModel)db.Children.First(c => c.Title == "film");

        await film.EnsureChildrenAsync();

        Assert.Equal(new[] { "film_id", "title" }, film.Children.Select(c => c.Title));
        Assert.All(film.Children, c => Assert.IsType<ColumnNodeViewModel>(c));
        // Columns are drawn from the already-loaded snapshot — no extra object read.
        Assert.Equal(1, browser.ObjectCalls);
    }

    [Fact]
    public async Task Refresh_reloads_an_expanded_node()
    {
        var browser = new FakeSchemaBrowser();
        var server = new ServerNodeViewModel(Conn(), browser);
        server.IsExpanded = true;
        await server.EnsureChildrenAsync();
        Assert.Equal(1, browser.DatabaseCalls);

        await server.RefreshAsync();

        // Reloaded while staying expanded — a second fetch happened and children are back.
        Assert.Equal(2, browser.DatabaseCalls);
        Assert.True(server.IsExpanded);
        Assert.Equal(new[] { "app", "other" }, server.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Refresh_of_collapsed_node_defers_reload_until_next_expand()
    {
        var browser = new FakeSchemaBrowser();
        var server = new ServerNodeViewModel(Conn(), browser);
        await server.EnsureChildrenAsync(); // loaded once, not expanded
        Assert.Equal(1, browser.DatabaseCalls);

        await server.RefreshAsync(); // collapsed → no immediate fetch, placeholder restored
        Assert.Equal(1, browser.DatabaseCalls);
        Assert.IsType<MessageNodeViewModel>(Assert.Single(server.Children));

        await server.EnsureChildrenAsync();
        Assert.Equal(2, browser.DatabaseCalls);
    }

    [Fact]
    public async Task Load_failure_yields_error_node()
    {
        var browser = new FakeSchemaBrowser { ThrowOnDatabases = true };
        var server = new ServerNodeViewModel(Conn(), browser);

        await server.EnsureChildrenAsync();

        var only = Assert.Single(server.Children);
        var msg = Assert.IsType<MessageNodeViewModel>(only);
        Assert.Contains("boom", msg.Title);
    }

    /// <summary>Objects spread over three schemas, so prefixing and ordering are observable.</summary>
    // ---- #132: the two shapes -------------------------------------------------------------------

    private static DatabaseNodeViewModel Database(MultiSchemaBrowser browser, SchemaTreeMode mode)
        => new(Conn(), "app", isConnected: true, browser, () => mode);

    private static SchemaGroupNodeViewModel Group(SchemaNodeViewModel node, string title)
        => node.Children.OfType<SchemaGroupNodeViewModel>().Single(g => g.Title == title);

    [Fact]
    public async Task Full_mode_puts_the_schemas_first_and_the_relations_inside_them()
    {
        var db = Database(new MultiSchemaBrowser(), SchemaTreeMode.Full);
        await db.EnsureChildrenAsync();

        // No relations at this level at all: that is the trade full mode makes.
        Assert.Empty(db.Children.OfType<RelationNodeViewModel>());
        Assert.Equal(["Schemas"], db.Children.Select(c => c.Title));

        var schemas = Group(db, "Schemas");
        Assert.Equal(["public", "audit", "billing"], schemas.Children.Select(c => c.Title));

        // A schema is lazy — it holds a placeholder until it is opened, which is what keeps a database with
        // two thousand relations cheap.
        var publicSchema = schemas.Children.First();
        Assert.IsType<MessageNodeViewModel>(publicSchema.Children.Single());

        await publicSchema.EnsureChildrenAsync();
        // The full set, in the shape helper's order — the kinds are there too, because the (empty) kind
        // read has landed, and an empty kind is a row with an em-dash rather than an absent one.
        Assert.Equal(SchemaTreeShape.SchemaGroupTitles, publicSchema.Children.Select(c => c.Title));
        Assert.Equal(["film"], Group(publicSchema, "Tables").Children.Select(c => c.Title));
    }

    [Fact]
    public async Task A_group_that_came_back_empty_says_so_rather_than_being_left_out()
    {
        // Inside a schema the set of rows is fixed, so it can be scanned; an em-dash is how a kind with
        // nothing in it differs from one whose read has not landed (#132).
        var db = Database(new MultiSchemaBrowser { ViewsAndRoutines = false }, SchemaTreeMode.Full);
        await db.EnsureChildrenAsync();

        var publicSchema = Group(db, "Schemas").Children.First();
        await publicSchema.EnsureChildrenAsync();

        Assert.Equal("—", Group(publicSchema, "Views").Detail);
        Assert.Equal("—", Group(publicSchema, "Procedures").Detail);
        Assert.Equal("1", Group(publicSchema, "Tables").Detail);
    }

    [Fact]
    public async Task A_single_schema_database_collapses_the_schema_level()
    {
        // Simple mode skips the Schemas row for one schema because the relations are already inline. Here
        // the level is the only route to them, so it collapses instead: the schema's groups move up.
        var db = Database(new MultiSchemaBrowser { OneSchema = true }, SchemaTreeMode.Full);
        await db.EnsureChildrenAsync();

        Assert.DoesNotContain("Schemas", db.Children.Select(c => c.Title));
        Assert.Equal(SchemaTreeShape.SchemaGroupTitles, db.Children.Select(c => c.Title));
        Assert.Equal(["film"], Group(db, "Tables").Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Switching_mode_rearranges_what_is_already_there_and_reads_nothing()
    {
        // The whole reason the node keeps what it read: a display preference must not cost a round trip per
        // database, and re-reading would also race the late reads (§9.8).
        var browser = new MultiSchemaBrowser();
        var mode = SchemaTreeMode.Simple;
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, browser, () => mode);

        await db.EnsureChildrenAsync();
        Assert.Contains(db.Children, c => c is RelationNodeViewModel);
        var callsAfterLoad = browser.ObjectCalls;

        mode = SchemaTreeMode.Full;
        db.ApplyMode();

        Assert.Equal(["Schemas"], db.Children.Select(c => c.Title));
        Assert.Equal(callsAfterLoad, browser.ObjectCalls);

        // And back again, to the shape it started in.
        mode = SchemaTreeMode.Simple;
        db.ApplyMode();
        Assert.Contains(db.Children, c => c is RelationNodeViewModel);
        Assert.Equal(callsAfterLoad, browser.ObjectCalls);
    }

    [Fact]
    public async Task A_relation_under_a_schema_opened_later_still_gets_its_size()
    {
        // The repair full mode needed. The size read relabels the rows that exist, and in full mode the rows
        // under an unopened schema do not — so a one-shot pass would leave every one of them unlabelled.
        var browser = new MultiSchemaBrowser { Sizes = [new RelationSize(1, TotalBytes: 2048, TableBytes: 2048, IndexBytes: 0, ToastBytes: 0, EstimatedRows: 12)] };
        var db = Database(browser, SchemaTreeMode.Full);
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        db.SizesLoaded = () => landed.TrySetResult();

        await db.EnsureChildrenAsync();
        await Task.WhenAny(landed.Task, Task.Delay(5000));

        // Opened after the sizes landed, which is the case that used to lose them.
        var publicSchema = Group(db, "Schemas").Children.First();
        await publicSchema.EnsureChildrenAsync();

        var film = Group(publicSchema, "Tables").Children.Single();
        Assert.Contains("2.0 kB", film.Detail);
    }

    [Fact]
    public async Task A_table_three_levels_down_is_still_findable()
    {
        // What F12 / "go to table" walks (§9.11). The walk used to flatten exactly one group level, which is
        // enough for simple mode's buckets and not for full mode's schemas.
        var db = Database(new MultiSchemaBrowser(), SchemaTreeMode.Full);
        await db.EnsureChildrenAsync();

        // Not yet: the schema holding it has never been opened, so the row genuinely does not exist. The
        // walk must not force it into being — that is the caller's job, and it is why RevealRelationAsync
        // expands the schema on the way down.
        Assert.Null(SchemaTreeReveal.RelationUnder(db, "audit", "events"));

        var audit = Group(db, "Schemas").Children.Single(c => c.Title == "audit");
        await audit.EnsureChildrenAsync();

        var found = SchemaTreeReveal.RelationUnder(db, "audit", "events");
        Assert.NotNull(found);
        Assert.Equal("events", found!.RelationName);
    }

    private sealed class MultiSchemaBrowser : ISchemaBrowser
    {
        public string[] SearchPath = new[] { "public" };
        public bool ViewsAndRoutines = true;
        public bool OneSchema;

        /// <summary>Sizes the late read reports, and kinds for the buckets. Both default to nothing, so the
        /// tests that are about arrangement are not also about them.</summary>
        public IReadOnlyList<RelationSize> Sizes = [];
        public DatabaseObjectKinds Kinds = DatabaseObjectKinds.Empty;

        /// <summary>How many times the catalog was asked. A re-arrange must not add to it (#132).</summary>
        public int ObjectCalls;

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "app" });

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
        {
            ObjectCalls++;
            var tables = OneSchema
                ? [new TableInfo(1, "public", "film", RelationKind.Table)]
                : new[]
                {
                    new TableInfo(1, "public", "film", RelationKind.Table),
                    new TableInfo(3, "audit", "film", RelationKind.Table),
                    new TableInfo(4, "audit", "events", RelationKind.Table),
                    new TableInfo(5, "billing", "invoice", RelationKind.Table),
                };
            if (ViewsAndRoutines)
                tables = tables.Append(new TableInfo(2, "public", "film_list", RelationKind.View)).ToArray();
            var schemas = OneSchema
                ? SearchPath.Concat(["public"]).Distinct().ToArray()
                : SearchPath.Concat(["public", "audit", "billing"]).Distinct().ToArray();
            var snapshot = new SchemaSnapshot(database, schemas, tables, Array.Empty<ColumnInfo>(),
                Array.Empty<ForeignKeyInfo>(), searchPath: SearchPath);
            var routines = ViewsAndRoutines
                ? new[] { new RoutineInfo(10, "public", "calc", RoutineKind.Function, "a integer", "integer") }
                : Array.Empty<RoutineInfo>();
            return Task.FromResult(new DatabaseObjects(snapshot, routines));
        }

        public Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult(TableDetails.Empty);

        public Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult("select 1");

        public Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(Sizes);

        public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DatabaseSize>>([]);

        /// <summary>#119's per-database kinds. Empty here: these fixtures exist for other questions, and a
        /// group they never assert on would only add noise to the trees they build.</summary>
        public Task<DatabaseObjectKinds> GetDatabaseObjectKindsAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(Kinds);

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

        public Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("CREATE FUNCTION calc() ...");

        public Task InvalidateAsync(Guid connectionId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSchemaBrowser : ISchemaBrowser
    {
        public int DatabaseCalls;
        public int ObjectCalls;
        public bool ThrowOnDatabases;

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
        {
            DatabaseCalls++;
            if (ThrowOnDatabases) throw new InvalidOperationException("boom");
            return Task.FromResult<IReadOnlyList<string>>(new[] { "app", "other" });
        }

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
        {
            ObjectCalls++;
            var tables = new[]
            {
                new TableInfo(1, "public", "film", RelationKind.Table),
                new TableInfo(2, "public", "zebra", RelationKind.Table),
                new TableInfo(3, "public", "film_list", RelationKind.View),
            };
            var columns = new[]
            {
                new ColumnInfo(1, 1, "film_id", "integer", NotNull: true, IsPrimaryKey: true),
                new ColumnInfo(1, 2, "title", "text", NotNull: false, IsPrimaryKey: false),
            };
            var snapshot = new SchemaSnapshot(database, new[] { "public" }, tables, columns, Array.Empty<ForeignKeyInfo>());
            var routines = new[] { new RoutineInfo(10, "public", "calc", RoutineKind.Function, "a integer", "integer") };
            return Task.FromResult(new DatabaseObjects(snapshot, routines));
        }

        public Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult(TableDetails.Empty);

        public Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct)
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

        public Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("CREATE FUNCTION calc() ...");

        public int InvalidateCalls;
        public Task InvalidateAsync(Guid connectionId) { InvalidateCalls++; return Task.CompletedTask; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
