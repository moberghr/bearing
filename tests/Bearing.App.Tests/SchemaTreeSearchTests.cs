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

/// <summary>
/// The schema tree's type-ahead reach. The rule under test is that a node's expansion state does not decide
/// whether the search sees it — only whether the search has to open something to show it.
/// </summary>
public class SchemaTreeSearchTests
{
    private static ConnectionInfo Conn()
        => new() { Id = Guid.NewGuid(), Name = "srv", ProviderId = "postgres", Host = "h", Port = 5432, Database = "app", User = "u" };

    /// <summary>A database with its film table expanded once (columns loaded) and then collapsed again.</summary>
    private static async Task<DatabaseNodeViewModel> LoadedDatabaseAsync()
    {
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, new Browser());
        await db.EnsureChildrenAsync();
        var film = (RelationNodeViewModel)db.Children.First(c => c.Title == "film");
        await film.EnsureChildrenAsync();
        film.IsExpanded = false;
        return db;
    }

    [Fact]
    public async Task A_collapsed_nodes_children_are_still_searchable()
    {
        var db = await LoadedDatabaseAsync();

        var matches = SchemaTreeSearch.Matches(new[] { (SchemaNodeViewModel)db }, "title");

        // film.title is loaded but hidden; leaving it out is what made the highlight disagree with the search.
        Assert.Equal(new[] { "title" }, matches.Select(m => m.Title));
    }

    [Fact]
    public async Task A_collapsed_bucket_members_are_searchable_without_expanding_it()
    {
        var db = await LoadedDatabaseAsync();
        var views = db.Children.OfType<SchemaGroupNodeViewModel>().First(g => g.Title == "Views");
        Assert.False(views.IsExpanded);

        var matches = SchemaTreeSearch.Matches(new[] { (SchemaNodeViewModel)db }, "film_list");

        Assert.Equal(new[] { "film_list" }, matches.Select(m => m.Title));
    }

    /// <summary>A never-expanded node holds only its placeholder, which must never surface as a match.</summary>
    [Fact]
    public void The_loading_placeholder_is_not_a_searchable_node()
    {
        var server = new ServerNodeViewModel(Conn(), new Browser());
        Assert.IsType<MessageNodeViewModel>(Assert.Single(server.Children));

        var all = SchemaTreeSearch.Flatten(new[] { (SchemaNodeViewModel)server });

        Assert.Equal(new[] { "srv" }, all.Select(n => n.Title));
        Assert.Empty(SchemaTreeSearch.Matches(new[] { (SchemaNodeViewModel)server }, "load"));
    }

    [Fact]
    public async Task Ancestors_of_a_hidden_match_are_what_has_to_be_expanded()
    {
        var db = await LoadedDatabaseAsync();
        var roots = new[] { (SchemaNodeViewModel)db };
        var hidden = SchemaTreeSearch.Matches(roots, "title").Single();

        var ancestors = SchemaTreeSearch.AncestorsOf(roots, hidden);

        // Outermost first, and the match itself is not in the chain.
        Assert.Equal(new[] { "app", "film" }, ancestors.Select(a => a.Title));
        foreach (var a in ancestors) a.IsExpanded = true;
        Assert.True(db.Children.First(c => c.Title == "film").IsExpanded);
    }

    [Fact]
    public async Task A_node_outside_the_tree_has_no_ancestor_chain()
    {
        var db = await LoadedDatabaseAsync();
        var stranger = new MessageNodeViewModel("", "elsewhere");

        Assert.Empty(SchemaTreeSearch.AncestorsOf(new[] { (SchemaNodeViewModel)db }, stranger));
    }

    /// <summary>The reported bug: search for "film" while the film table is collapsed, then expand it — its
    /// film_id column matched the query but came up unhighlighted, because it did not exist when the search
    /// ran and nothing tested it afterwards.</summary>
    [Fact]
    public async Task Children_loaded_after_a_search_ran_highlight_themselves()
    {
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, new Browser());
        await db.EnsureChildrenAsync();
        var film = (RelationNodeViewModel)db.Children.First(c => c.Title == "film");

        // What the sidebar's search pass leaves behind on every loaded node.
        foreach (var n in SchemaTreeSearch.Flatten(new[] { (SchemaNodeViewModel)db }))
            n.MatchTest = t => SchemaTreeSearch.FuzzyMatch(t, "film");

        await film.EnsureChildrenAsync(); // columns arrive only now

        Assert.True(film.Children.First(c => c.Title == "film_id").IsMatch);
        Assert.False(film.Children.First(c => c.Title == "title").IsMatch);
    }

    /// <summary>A bucket is handed its members at construction, so they need the same treatment one level in.</summary>
    [Fact]
    public async Task Bucket_members_loaded_after_a_search_ran_highlight_themselves()
    {
        var server = new ServerNodeViewModel(Conn(), new Browser());
        server.MatchTest = t => SchemaTreeSearch.FuzzyMatch(t, "film_list");

        await server.EnsureChildrenAsync();
        var db = (DatabaseNodeViewModel)server.Children.Single();
        await db.EnsureChildrenAsync();

        var views = db.Children.OfType<SchemaGroupNodeViewModel>().Single(g => g.Title == "Views");
        Assert.True(views.Children.Single().IsMatch);
        Assert.False(views.IsMatch);
    }

    /// <summary>The placeholder is not a row anyone searched for; tinting it would highlight "Loading…".</summary>
    [Fact]
    public async Task The_loading_placeholder_never_highlights()
    {
        var server = new ServerNodeViewModel(Conn(), new Browser());
        server.MatchTest = _ => true;

        await server.EnsureChildrenAsync();

        var db = (DatabaseNodeViewModel)server.Children.Single();
        Assert.True(db.IsMatch);
        Assert.False(Assert.IsType<MessageNodeViewModel>(db.Children.Single()).IsMatch);
    }

    [Theory]
    [InlineData("film", "flm", true)]           // subsequence, not substring
    [InlineData("audit.events", "events", true)] // a schema prefix does not hide the name
    [InlineData("film", "mlif", false)]          // order matters
    [InlineData("film", "", false)]              // an empty query matches nothing, not everything
    public void Fuzzy_match_is_an_ordered_case_insensitive_subsequence(string text, string query, bool expected)
        => Assert.Equal(expected, SchemaTreeSearch.FuzzyMatch(text, query));

    private sealed class Browser : ISchemaBrowser
    {
        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "app" });

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
        {
            var tables = new[]
            {
                new TableInfo(1, "public", "film", RelationKind.Table),
                new TableInfo(2, "public", "film_list", RelationKind.View),
            };
            var columns = new[]
            {
                new ColumnInfo(1, 1, "film_id", "integer", NotNull: true, IsPrimaryKey: true),
                new ColumnInfo(1, 2, "title", "text", NotNull: false, IsPrimaryKey: false),
            };
            var snapshot = new SchemaSnapshot(database, new[] { "public" }, tables, columns, Array.Empty<ForeignKeyInfo>());
            return Task.FromResult(new DatabaseObjects(snapshot, Array.Empty<RoutineInfo>()));
        }

        public Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult("select 1");

        public Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("");

        public Task InvalidateAsync(Guid connectionId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
