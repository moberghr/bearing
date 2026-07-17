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

    private MainWindowViewModel NewVm() => new(
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
        await vm.SeedDemoConnectionAsync(Host, Port, Db, User, Password);

        // One named connection, assigned to the restored/seeded tab.
        var conn = Assert.Single(vm.Connections);
        Assert.Equal($"{Db} (local)", conn.Name);
        Assert.Equal("local", conn.Environment);
        Assert.NotNull(vm.SelectedTab);
        Assert.Equal(conn.Id, vm.SelectedTab!.ConnectionId);

        // Execute against the tab's connection.
        vm.SelectedTab.Text = "select count(*) from film;";
        await vm.ExecuteAsync(vm.SelectedTab.Text);
        Assert.True(vm.SelectedTab.LastResult?.Success, vm.StatusText);
        Assert.Equal(1, vm.SelectedTab.LastResult!.RowCount);

        // A new tab inherits the current tab's connection.
        var t2 = vm.NewTab();
        Assert.Equal(conn.Id, t2.ConnectionId);
        await vm.ExecuteAsync("select 1;");
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

    [Fact]
    public async Task Rename_scratch_and_saved_script()
    {
        var dir = Path.Combine(_root, "renproj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);

        // Scratch rename is a label only.
        var tab = vm.SelectedTab!;
        Assert.StartsWith("Scratch", tab.DisplayName);
        await vm.RenameTabAsync(tab, "My analysis");
        Assert.Equal("My analysis", tab.Header);

        // Save it, then rename the file on disk.
        var path = Path.Combine(vm.ScriptsDirectory!, "daily.sql");
        await vm.SaveSelectedScriptAsync(path, "select 1;");
        Assert.Contains(vm.Scripts, s => s.Name == "daily.sql");

        await vm.RenameScriptAsync(path, "weekly");
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(vm.ScriptsDirectory!, "weekly.sql")));
        Assert.Equal("weekly.sql", tab.Header);
        Assert.Contains(vm.Scripts, s => s.Name == "weekly.sql");
        Assert.DoesNotContain(vm.Scripts, s => s.Name == "daily.sql");
    }

    private static async Task<Core.Schema.ISchemaSnapshot?> WaitForSnapshot(MainWindowViewModel vm)
    {
        for (var i = 0; i < 50; i++)
        {
            var snap = vm.SnapshotForSelectedTab();
            if (snap is not null) return snap;
            await Task.Delay(100);
        }
        return vm.SnapshotForSelectedTab();
    }
}
