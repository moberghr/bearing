using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
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
        Assert.All(db.Children, c => Assert.IsType<RelationNodeViewModel>(c));
    }

    /// <summary>The schema is on the row for anything outside the default schema, and those rows sort last.
    /// Without it, two same-named tables in different schemas were indistinguishable in the list.</summary>
    [Fact]
    public async Task Objects_outside_the_default_schema_are_prefixed_and_sort_below_it()
    {
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, new MultiSchemaBrowser());

        await db.EnsureChildrenAsync();

        Assert.Equal(
            new[] { "film", "audit.events", "audit.film", "billing.invoice", "Views", "Functions" },
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
            new[] { "events", "film", "billing.invoice", "public.film", "Views", "Functions" },
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
    private sealed class MultiSchemaBrowser : ISchemaBrowser
    {
        public string[] SearchPath = new[] { "public" };
        public bool ViewsAndRoutines = true;

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "app" });

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
        {
            var tables = new[]
            {
                new TableInfo(1, "public", "film", RelationKind.Table),
                new TableInfo(3, "audit", "film", RelationKind.Table),
                new TableInfo(4, "audit", "events", RelationKind.Table),
                new TableInfo(5, "billing", "invoice", RelationKind.Table),
            };
            if (ViewsAndRoutines)
                tables = tables.Append(new TableInfo(2, "public", "film_list", RelationKind.View)).ToArray();
            var schemas = SearchPath.Concat(new[] { "public", "audit", "billing" }).Distinct().ToArray();
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

        public Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("CREATE FUNCTION calc() ...");

        public int InvalidateCalls;
        public Task InvalidateAsync(Guid connectionId) { InvalidateCalls++; return Task.CompletedTask; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
