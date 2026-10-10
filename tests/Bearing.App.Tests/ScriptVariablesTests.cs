using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// <c>:name</c> script variables on the run path: asked for before anything else reads the SQL, so the
/// guard, the refusal and the executor all see the values that were typed. Which colons count is
/// <c>SqlVariablesTests</c>' subject; this is what <see cref="ExecutionViewModel"/> does with them.
/// </summary>
public class ScriptVariablesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-vars", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private sealed record Harness(
        WorkspaceContext Ctx, ExecutionViewModel Exec, FakeExecutor Executor, FakeDialogs Dialogs,
        EditorTabViewModel Tab, List<string> Status);

    private Harness NewHarness(bool readOnly = false, bool variables = true)
    {
        Directory.CreateDirectory(_root);
        var executor = new FakeExecutor();
        var dialogs = new FakeDialogs();
        var ctx = new WorkspaceContext(
            new FakeProvider { Executor = executor },
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(
                new AppSettings { AutosaveMode = AutosaveMode.Off, ScriptVariables = variables }));
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "prod",
            ProviderId = "postgres",
            Database = "app",
            ReadOnly = readOnly,
        };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        var status = new List<string>();
        ctx.Status = status.Add;
        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        return new Harness(ctx, new ExecutionViewModel(ctx, dialogs), executor, dialogs, tab, status);
    }

    [Fact]
    public async Task The_values_typed_are_what_reaches_the_server()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["id"] = "42", ["name"] = "Ada" };

        await h.Exec.ExecuteAsync("select * from t where id = :id and name = :'name'");

        Assert.Equal(["id", "name"], Assert.Single(h.Dialogs.VariablePrompts).Names);
        Assert.StartsWith("select * from t where id = 42 and name = 'Ada'", Assert.Single(h.Executor.Executed));
    }

    [Fact]
    public async Task A_rerun_offers_the_last_values_typed_on_that_tab()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["id"] = "42" };

        await h.Exec.ExecuteAsync("select :id");
        await h.Exec.ExecuteAsync("select :ID");

        Assert.Empty(h.Dialogs.VariablePrompts[0].Remembered);
        Assert.Equal("42", h.Dialogs.VariablePrompts[1].Remembered["id"]);
    }

    [Fact]
    public async Task Cancelling_the_prompt_runs_nothing()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = null;

        await h.Exec.ExecuteAsync("select :id");

        Assert.Empty(h.Executor.Executed);
        Assert.Contains("Cancelled — variables not filled in.", h.Status);
    }

    /// <summary>The reason substitution happens before the guard: a value is SQL, and a value that writes
    /// is refused on a read-only connection exactly as the same text typed into the editor would be.</summary>
    [Fact]
    public async Task A_value_that_writes_is_refused_on_a_read_only_connection()
    {
        var h = NewHarness(readOnly: true);
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["id"] = "1; delete from t" };

        await h.Exec.ExecuteAsync("select * from t where id = :id");

        Assert.Empty(h.Executor.Executed);
        Assert.Contains(h.Status, s => s.Contains("read-only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sql_without_variables_is_not_prompted_for()
    {
        var h = NewHarness();

        await h.Exec.ExecuteAsync("select '1'::int, ':x' -- :y");

        Assert.Empty(h.Dialogs.VariablePrompts);
        Assert.Single(h.Executor.Executed);
    }

    [Fact]
    public async Task With_the_setting_off_a_colon_name_is_sent_as_written()
    {
        var h = NewHarness(variables: false);

        await h.Exec.ExecuteAsync("select :id");

        Assert.Empty(h.Dialogs.VariablePrompts);
        Assert.StartsWith("select :id", Assert.Single(h.Executor.Executed));
    }
}
