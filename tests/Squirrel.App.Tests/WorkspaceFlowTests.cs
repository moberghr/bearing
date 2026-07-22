using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.App.ViewModels;
using Squirrel.Core.Data;
using Squirrel.Data;
using Squirrel.Data.Postgres;
using Squirrel.Persistence;
using Xunit;

namespace Squirrel.App.Tests;

/// <summary>
/// End-to-end drive of the shell view-model against live pagila: multiple named connections,
/// per-tab connection execution, connection inheritance on new tabs, session round-trip, and
/// script rename. Skips if no PostgreSQL is reachable.
/// </summary>
public class WorkspaceFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "squirrel-flow", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static string Env(string key, string dflt) => Environment.GetEnvironmentVariable($"SQUIRREL_TEST_PG_{key}") ?? dflt;
    private static string Host => Env("HOST", "localhost");
    private static int Port => int.Parse(Env("PORT", "5434"));
    private static string Db => Env("DB", "pagila");
    private static string User => Env("USER", "postgres");
    private static string Password => Env("PASSWORD", "squirrel");

    private ShellViewModel NewVm() => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FileFallbackSecretStore(Path.Combine(_root, "secrets")));

    private static async Task<bool> Reachable()
    {
        var info = new ConnectionInfo { Id = Guid.NewGuid(), Name = "probe", ProviderId = PostgresProvider.ProviderId,
            Host = Host, Port = Port, Database = Db, User = User };
        await using var f = new ProviderRegistry().Get(PostgresProvider.ProviderId).CreateConnectionFactory(info, Password);
        try { return await f.TestConnectionAsync(CancellationToken.None); } catch { return false; }
    }

    [SkippableFact]
    public async Task Per_tab_connection_executes_inherits_and_round_trips()
    {
        Skip.IfNot(await Reachable(), "No PostgreSQL reachable for integration test.");

        var dir = Path.Combine(_root, "proj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);
        await vm.Connections.SeedDemoConnectionAsync(Host, Port, Db, User, Password);

        // One named connection, assigned to the restored/seeded tab.
        var conn = Assert.Single(vm.Connections.Connections);
        Assert.Equal($"{Db} (local)", conn.Name);
        Assert.Equal("local", conn.Environment);
        Assert.NotNull(vm.Workspace.SelectedTab);
        Assert.Equal(conn.Id, vm.Workspace.SelectedTab!.ConnectionId);

        // Execute against the tab's connection.
        vm.Workspace.SelectedTab.Text = "select count(*) from film;";
        await vm.Execution.ExecuteAsync(vm.Workspace.SelectedTab.Text);
        Assert.True(vm.Workspace.SelectedTab.LastResult?.Success, vm.StatusText);
        Assert.Equal(1, vm.Workspace.SelectedTab.LastResult!.RowCount);

        // A new tab inherits the current tab's connection.
        var t2 = vm.Workspace.NewTab();
        Assert.Equal(conn.Id, t2.ConnectionId);
        await vm.Execution.ExecuteAsync("select 1;");
        Assert.True(t2.LastResult?.Success, vm.StatusText);

        // Completion schema is available for the selected tab's connection after a run.
        var snapshot = await WaitForSnapshot(vm);
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Tables, t => t.Name == "film");

        // Session round-trip: per-tab connection ids survive save/reload.
        vm.SaveWorkspace();
        var reloaded = await new JsonSessionStore().LoadAsync(dir, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.All(reloaded!.OpenEditors, e => Assert.Equal(conn.Id, e.ConnectionId));
        Assert.True(reloaded.SidePaneOpen);

        await vm.DisposeSessionsAsync();
    }

    [SkippableFact]
    public async Task Single_select_pages_and_counts()
    {
        Skip.IfNot(await Reachable(), "No PostgreSQL reachable for integration test.");

        var dir = Path.Combine(_root, "pageproj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);
        await vm.Connections.SeedDemoConnectionAsync(Host, Port, Db, User, Password);

        // First page: a single row-returning SELECT is pageable and capped at PageSize.
        await vm.Execution.ExecuteAsync("select * from film order by film_id;");
        var rs = vm.Workspace.SelectedTab!.LastResult!;
        Assert.True(rs.Success, vm.StatusText);
        Assert.True(rs.IsPageable);
        Assert.Equal(ExecutionViewModel.PageSize, rs.Loaded);
        Assert.True(rs.HasMore);                       // pagila.film has 1000 rows
        Assert.Null(rs.TotalCount);

        // Load more appends the next page in place.
        await vm.Execution.LoadMoreAsync(rs);
        Assert.Equal(ExecutionViewModel.PageSize * 2, rs.Loaded);
        Assert.True(rs.HasMore);

        // Count fills in the total and retires the [Count] affordance.
        await vm.Execution.CountTotalAsync(rs);
        Assert.Equal(1000, rs.TotalCount);
        Assert.False(rs.CanCount);

        // A multi-statement run is not pageable.
        await vm.Execution.ExecuteAsync("select 1; select 2;");
        Assert.All(vm.Workspace.SelectedTab!.Results, r => Assert.False(r.IsPageable));

        await vm.DisposeSessionsAsync();
    }

    [SkippableFact]
    public async Task Foreign_key_cell_navigates_to_referenced_row()
    {
        Skip.IfNot(await Reachable(), "No PostgreSQL reachable for integration test.");

        var dir = Path.Combine(_root, "fkproj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);
        await vm.Connections.SeedDemoConnectionAsync(Host, Port, Db, User, Password);
        await WaitForSnapshot(vm); // FK detection needs the schema snapshot loaded

        // film.language_id is a foreign key into language; film.title is not.
        await vm.Execution.ExecuteAsync("select film_id, title, language_id from film order by film_id;");
        var rs = vm.Workspace.SelectedTab!.LastResult!;
        var names = rs.Columns.Select(c => c.Name).ToList();
        var langCol = names.IndexOf("language_id");
        Assert.True(langCol >= 0);
        Assert.Contains(langCol, rs.ForeignKeyColumns);
        Assert.DoesNotContain(names.IndexOf("title"), rs.ForeignKeyColumns);

        // Navigating the FK cell swaps the displayed result in place (no new tab) for the referenced row.
        var tabsBefore = vm.Workspace.Tabs.Count;
        await vm.Execution.NavigateForeignKeyAsync(rs, langCol, rs.Rows[0]);
        Assert.Equal(tabsBefore, vm.Workspace.Tabs.Count);   // navigation is inline, not a new tab
        Assert.True(vm.Workspace.SelectedTab!.CanGoBack);     // the film result is stacked behind
        var navResult = vm.Workspace.SelectedTab.LastResult!;
        Assert.True(navResult.Success, vm.StatusText);
        Assert.Equal(1, navResult.RowCount);        // language_id is a PK in language → exactly one row

        // Back discards the navigated result and restores the original film result set instance.
        vm.Workspace.SelectedTab.GoBack();
        Assert.False(vm.Workspace.SelectedTab.CanGoBack);
        Assert.Same(rs, vm.Workspace.SelectedTab.LastResult);

        await vm.DisposeSessionsAsync();
    }

    [SkippableFact]
    public async Task Editable_grid_saves_insert_update_delete_in_one_batch()
    {
        Skip.IfNot(await Reachable(), "No PostgreSQL reachable for integration test.");

        // Create + seed a test table via a separate connection BEFORE the VM connects, so it's in the
        // schema snapshot (editability is resolved from the snapshot).
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var info = new ConnectionInfo { Id = Guid.NewGuid(), Name = "setup", ProviderId = PostgresProvider.ProviderId,
            Host = Host, Port = Port, Database = Db, User = User };
        await using var setup = provider.CreateConnectionFactory(info, Password);
        var raw = provider.CreateQueryExecutor(setup);
        const string tbl = "squirrel_edit_test";
        await raw.ExecuteAsync($"drop table if exists {tbl}; create table {tbl} (id serial primary key, name text, qty int);",
            new QueryOptions(), CancellationToken.None);
        await raw.ExecuteAsync($"insert into {tbl} (name, qty) values ('one', 1), ('two', 2);", new QueryOptions(), CancellationToken.None);
        try
        {
            var vm = NewVm();
            await vm.InitializeAsync(Path.Combine(_root, "editproj"));
            await vm.Connections.SeedDemoConnectionAsync(Host, Port, Db, User, Password);
            await WaitForSnapshot(vm);

            vm.Workspace.SelectedTab!.Text = $"select id, name, qty from {tbl} order by id;";
            await vm.Execution.ExecuteAsync(vm.Workspace.SelectedTab.Text);
            var rs = vm.Workspace.SelectedTab.LastResult!;
            Assert.True(rs.IsEditable, vm.StatusText);
            Assert.Equal(2, rs.Rows.Count);

            var row0 = rs.Rows[0]; // (1, one, 1)
            var row1 = rs.Rows[1]; // (2, two, 2)

            row0[1] = "one-edited"; rs.MarkEdited(row0);      // UPDATE
            var added = rs.AddRow(); added[1] = "three"; added[2] = "3"; // INSERT (id serial → left null)
            rs.ToggleDelete(row1);                             // DELETE (row kept visible until save)
            Assert.Equal(3, rs.PendingCount);
            Assert.True(rs.IsRowDeleted(row1));
            Assert.Contains(row1, rs.Rows);                    // still present, just marked

            await vm.Execution.SaveChangesAsync(rs);
            var reloaded = vm.Workspace.SelectedTab.LastResult!;      // save swaps in a fresh, reloaded result set
            Assert.False(reloaded.HasPendingChanges, vm.StatusText);
            Assert.Equal(2, reloaded.Rows.Count);           // 2 original − 1 delete + 1 insert

            // Verify against an independent read: edit applied, delete gone, insert present.
            var check = await raw.ExecuteAsync($"select name, qty from {tbl} order by id;", new QueryOptions(), CancellationToken.None);
            var names = check[0].Rows.Select(r => (string?)r[0]).ToList();
            Assert.Equal(2, check[0].Rows.Count);
            Assert.Contains("one-edited", names);
            Assert.DoesNotContain("two", names);
            Assert.Contains("three", names);

            await vm.DisposeSessionsAsync();
        }
        finally
        {
            await raw.ExecuteAsync($"drop table if exists {tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Empty_saves_as_empty_for_text_and_null_token_saves_null()
    {
        Skip.IfNot(await Reachable(), "No PostgreSQL reachable for integration test.");

        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var info = new ConnectionInfo { Id = Guid.NewGuid(), Name = "setup", ProviderId = PostgresProvider.ProviderId,
            Host = Host, Port = Port, Database = Db, User = User };
        await using var setup = provider.CreateConnectionFactory(info, Password);
        var raw = provider.CreateQueryExecutor(setup);
        const string tbl = "squirrel_null_test";
        await raw.ExecuteAsync($"drop table if exists {tbl}; create table {tbl} (id serial primary key, name text, qty int);",
            new QueryOptions(), CancellationToken.None);
        try
        {
            var vm = NewVm();
            await vm.InitializeAsync(Path.Combine(_root, "nullproj"));
            await vm.Connections.SeedDemoConnectionAsync(Host, Port, Db, User, Password);
            await WaitForSnapshot(vm);

            vm.Workspace.SelectedTab!.Text = $"select id, name, qty from {tbl} order by id;";
            await vm.Execution.ExecuteAsync(vm.Workspace.SelectedTab.Text);
            var rs = vm.Workspace.SelectedTab.LastResult!;
            Assert.True(rs.IsEditable, vm.StatusText);

            // New row: empty text stays empty; the "(null)" token in an int column becomes SQL NULL.
            var added = rs.AddRow();
            added[1] = "";          // name (text) → empty string, NOT null
            added[2] = "(null)";    // qty (int)  → NULL
            await vm.Execution.SaveChangesAsync(rs);

            var check = await raw.ExecuteAsync($"select name, qty from {tbl};", new QueryOptions(), CancellationToken.None);
            var row = Assert.Single(check[0].Rows);
            Assert.Equal("", row[0]);   // empty string preserved
            Assert.Null(row[1]);        // explicit NULL

            // Editing the text cell to the null token clears it to NULL.
            var saved = vm.Workspace.SelectedTab.LastResult!.Rows[0];
            saved[1] = "(null)"; vm.Workspace.SelectedTab.LastResult!.MarkEdited(saved);
            await vm.Execution.SaveChangesAsync(vm.Workspace.SelectedTab.LastResult!);

            var check2 = await raw.ExecuteAsync($"select name from {tbl};", new QueryOptions(), CancellationToken.None);
            Assert.Null(Assert.Single(check2[0].Rows)[0]);

            await vm.DisposeSessionsAsync();
        }
        finally
        {
            await raw.ExecuteAsync($"drop table if exists {tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Rename_scratch_and_saved_script()
    {
        var dir = Path.Combine(_root, "renproj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);

        // Scratch rename is a label only.
        var tab = vm.Workspace.SelectedTab!;
        Assert.StartsWith("Scratch", tab.DisplayName);
        await vm.Workspace.RenameTabAsync(tab, "My analysis");
        Assert.Equal("My analysis", tab.Header);

        // Save it, then rename the file on disk.
        var path = Path.Combine(vm.ScriptsDirectory!, "daily.sql");
        await vm.Workspace.SaveSelectedScriptAsync(path, "select 1;");
        Assert.Contains(vm.Scripts.Scripts, s => s.Name == "daily.sql");

        await vm.Scripts.RenameScriptAsync(path, "weekly");
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(vm.ScriptsDirectory!, "weekly.sql")));
        Assert.Equal("weekly.sql", tab.Header);
        Assert.Contains(vm.Scripts.Scripts, s => s.Name == "weekly.sql");
        Assert.DoesNotContain(vm.Scripts.Scripts, s => s.Name == "daily.sql");
    }

    [Fact]
    public async Task Unsaved_script_edits_survive_reload_and_stay_marked_dirty()
    {
        var dir = Path.Combine(_root, "dirtyproj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);

        // Save a script, then edit its buffer without saving.
        var path = Path.Combine(vm.ScriptsDirectory!, "report.sql");
        await vm.Workspace.SaveSelectedScriptAsync(path, "select 1;");
        var tab = vm.Workspace.SelectedTab!;
        Assert.False(tab.IsDirty);

        tab.Text = "select 1; -- WIP";
        Assert.True(tab.IsDirty);

        // Persist the session and reload into a fresh VM (mimics project switch / app restart).
        vm.SaveWorkspace();
        var vm2 = NewVm();
        await vm2.InitializeAsync(dir);

        var restored = Assert.Single(vm2.Workspace.Tabs, t => t.Header == "report.sql");
        Assert.Equal("select 1; -- WIP", restored.Text);                 // unsaved edits preserved
        Assert.True(restored.IsDirty);                                   // still marked modified
        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));    // disk untouched

        // Saving writes disk and clears the marker.
        await vm2.Workspace.SaveSelectedScriptAsync(path, restored.Text);
        Assert.False(restored.IsDirty);
        Assert.Equal("select 1; -- WIP", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Resume_opens_most_recent_existing_project_and_skips_stale_entries()
    {
        var store = new JsonProjectStore();
        var recent = new FileRecentProjects(Path.Combine(_root, "recent.json"));
        var projA = Path.Combine(_root, "projA");
        var projB = Path.Combine(_root, "projB");
        var fallback = Path.Combine(_root, "default");
        await store.CreateAsync(projA, "Alpha", CancellationToken.None);
        await store.CreateAsync(projB, "Beta", CancellationToken.None);
        await recent.AddAsync(projA, CancellationToken.None);
        await recent.AddAsync(projB, CancellationToken.None);   // B is now most-recent

        // Resume reopens the most-recently-used project, not the fallback.
        var vm = NewVm();
        await vm.ResumeLastProjectAsync(fallback);
        Assert.Equal("Beta", vm.CurrentProjectName);
        Assert.Equal(Path.GetFullPath(projB), Path.GetFullPath(vm.ProjectDirectory!));
        Assert.False(Directory.Exists(fallback));               // fallback untouched when a resume succeeds

        // A since-deleted most-recent entry is skipped in favour of the next existing one.
        Directory.Delete(projB, recursive: true);
        var vm2 = NewVm();
        await vm2.ResumeLastProjectAsync(fallback);
        Assert.Equal("Alpha", vm2.CurrentProjectName);
        Assert.Equal(Path.GetFullPath(projA), Path.GetFullPath(vm2.ProjectDirectory!));
    }

    [Fact]
    public async Task Resume_falls_back_to_default_when_recent_list_is_empty()
    {
        var fallback = Path.Combine(_root, "default");
        var vm = NewVm();
        await vm.ResumeLastProjectAsync(fallback);
        Assert.Equal(Path.GetFullPath(fallback), Path.GetFullPath(vm.ProjectDirectory!));
        Assert.True(Directory.Exists(fallback));                // fallback project created on first run
    }

    [SkippableFact]
    public async Task Switching_database_runs_against_the_chosen_db_on_the_same_server()
    {
        Skip.IfNot(await Reachable(), "No PostgreSQL reachable for integration test.");

        var vm = NewVm();
        await vm.InitializeAsync(Path.Combine(_root, "dbswitch"));
        await vm.Connections.SeedDemoConnectionAsync(Host, Port, Db, User, Password);
        var tab = vm.Workspace.SelectedTab!;

        // Default DB is the connection's own.
        Assert.Equal(Db, tab.DatabaseName);

        // Switch to the always-present 'postgres' maintenance DB (same server, reused credentials).
        vm.Connections.SetTabDatabase(tab, "postgres");
        Assert.Equal("postgres", tab.DatabaseName);

        await vm.Execution.ExecuteAsync("select current_database();");
        Assert.True(tab.LastResult?.Success, vm.StatusText);
        Assert.Equal("postgres", tab.LastResult!.Rows[0][0]?.ToString());

        // Switch back — the original database's tables resolve again.
        vm.Connections.SetTabDatabase(tab, Db);
        await vm.Execution.ExecuteAsync("select current_database();");
        Assert.Equal(Db, tab.LastResult!.Rows[0][0]?.ToString());
    }

    private static async Task<Core.Schema.ISchemaSnapshot?> WaitForSnapshot(ShellViewModel vm)
    {
        for (var i = 0; i < 50; i++)
        {
            var snap = vm.Execution.SnapshotForSelectedTab();
            if (snap is not null) return snap;
            await Task.Delay(100);
        }
        return vm.Execution.SnapshotForSelectedTab();
    }
}
