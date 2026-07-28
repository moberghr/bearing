using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.App.Connections;
using Squirrel.App.ViewModels;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;
using Xunit;

namespace Squirrel.App.Tests;

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

    [Fact]
    public async Task Database_node_lists_relations_then_routines_sorted()
    {
        var browser = new FakeSchemaBrowser();
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, browser);

        await db.EnsureChildrenAsync();

        // Tables (film, zebra) before the view (film_list) before the function (calc).
        Assert.Equal(new[] { "film", "zebra", "film_list", "calc" }, db.Children.Select(c => c.Title));
        Assert.IsType<RelationNodeViewModel>(db.Children[0]);
        Assert.IsType<RoutineNodeViewModel>(db.Children[3]);
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

        public Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult("select 1");

        public Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("CREATE FUNCTION calc() ...");

        public int InvalidateCalls;
        public Task InvalidateAsync(Guid connectionId) { InvalidateCalls++; return Task.CompletedTask; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
