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
/// <c>:name</c> variables on the run path (#103): values come from <c>@set</c> lines or the prompt and are
/// <b>bound</b>, never spliced into the text — so what the server, the guard and the log see is the text the
/// user wrote, with placeholders. Which colons count is <c>SqlVariablesTests</c>' subject, which lines are
/// <c>@set</c> is <c>ScriptDirectivesTests</c>'; this is what <see cref="ExecutionViewModel"/> does with them.
/// </summary>
public class ScriptVariablesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-vars", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private sealed record Harness(
        WorkspaceContext Ctx, ExecutionViewModel Exec, FakeExecutor Executor, FakeDialogs Dialogs,
        EditorTabViewModel Tab, List<string> Status)
    {
        /// <summary>The newest log entry, once the background writer has stored it.</summary>
        public async Task<Bearing.Core.Logging.QueryLogEntry> LoggedAsync()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var rows = await Ctx.QueryLog.SearchAsync(new Bearing.Core.Logging.QueryLogQuery(), default);
                if (rows.Count > 0) return rows[0];
                await Task.Delay(20);
            }
            throw new InvalidOperationException("nothing was logged");
        }
    }

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
    public async Task Values_are_bound_to_placeholders_and_never_spliced_into_the_text()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["id"] = "42", ["name"] = "Ada" };

        await h.Exec.ExecuteAsync("select * from t where id = :id and name = :'name'");

        Assert.Equal(["id", "name"], Assert.Single(h.Dialogs.VariablePrompts).Select(f => f.Name));
        Assert.StartsWith("select * from t where id = @bearing_id and name = @bearing_name", Assert.Single(h.Executor.Executed));
        Assert.Equal(
            [new QueryParameter("bearing_id", "42", QueryParameterKind.Number),
             new QueryParameter("bearing_name", "Ada", QueryParameterKind.Text)],
            Assert.Single(h.Executor.ExecutedParameters));
    }

    [Fact]
    public async Task Set_values_are_shown_filled_in_and_marked_and_never_reach_the_server_as_text()
    {
        var h = NewHarness();

        await h.Exec.ExecuteAsync("@set id = 42\nselect * from t where id = :id");

        var field = Assert.Single(Assert.Single(h.Dialogs.VariablePrompts));
        Assert.Equal(("id", "42", "@set, line 1"), (field.Name, field.Initial, field.Note));
        Assert.DoesNotContain("@set", Assert.Single(h.Executor.Executed));
        Assert.Equal("42", Assert.Single(Assert.Single(h.Executor.ExecutedParameters)).Value);
    }

    [Fact]
    public async Task A_set_value_can_be_overridden_for_the_run()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["id"] = "7" };

        await h.Exec.ExecuteAsync("@set id = 42\nselect * from t where id = :id");

        Assert.Equal("7", Assert.Single(Assert.Single(h.Executor.ExecutedParameters)).Value);
        Assert.Equal("@set id = 7\nselect * from t where id = :id", (await h.LoggedAsync()).SqlText);
    }

    /// <summary>The reported case: a <c>@set</c> further down the script must not reach a statement above it.</summary>
    [Fact]
    public async Task A_set_line_below_a_statement_does_not_apply_to_it()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["id"] = "1" };

        await h.Exec.ExecuteAsync("select :id;\n@set id = 2\nselect :id;");

        var fields = Assert.Single(h.Dialogs.VariablePrompts);
        Assert.Equal([("id", null), ("id", "@set, line 2")], fields.Select(f => (f.Name, f.Note)));
        Assert.Equal(["bearing_id", "bearing_id__2"], Assert.Single(h.Executor.ExecutedParameters).Select(p => p.Name));
    }

    [Fact]
    public async Task Set_lines_above_the_statement_that_runs_count_with_their_buffer_line()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["staff"] = "2" };

        await h.Exec.ExecuteAsync("select * from t where id = :id and staff = :staff",
            preamble: "@set id = 7\nselect 1;\n");

        Assert.Equal([("id", "@set, line 1"), ("staff", null)],
            Assert.Single(h.Dialogs.VariablePrompts).Select(f => (f.Name, f.Note)));
        Assert.Equal(["7", "2"], Assert.Single(h.Executor.ExecutedParameters).Select(p => p.Value));
    }

    [Fact]
    public async Task The_log_keeps_the_text_as_written_with_the_values_beside_it()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["email"] = "ada@example.com" };

        await h.Exec.ExecuteAsync("@set id = 42\nselect * from t where id = :id and email = :email");

        var logged = await h.LoggedAsync();
        Assert.Equal("@set id = 42\nselect * from t where id = :id and email = :email", logged.SqlText);
        Assert.Equal(["email = ada@example.com"], logged.Parameters.Select(p => $"{p.Name} = {p.Value}"));
    }

    [Fact]
    public async Task A_result_reports_the_query_with_its_values_set_above_it()
    {
        var h = NewHarness();

        await h.Exec.ExecuteAsync("@set id = 42\nselect * from t where id = :id");

        Assert.Equal("@set id = 42\nselect * from t where id = :id", h.Tab.LastResult!.ExecutedSql);
    }

    [Fact]
    public async Task A_rerun_offers_the_last_values_typed_on_that_tab()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["id"] = "42" };

        await h.Exec.ExecuteAsync("select :id");
        await h.Exec.ExecuteAsync("select :ID");

        Assert.Equal("", Assert.Single(h.Dialogs.VariablePrompts[0]).Initial);
        Assert.Equal("42", Assert.Single(h.Dialogs.VariablePrompts[1]).Initial);
    }

    [Fact]
    public async Task Cancelling_the_prompt_runs_nothing()
    {
        var h = NewHarness();
        h.Dialogs.CancelVariables = true;

        await h.Exec.ExecuteAsync("@set id = 1\nselect :id");

        Assert.Empty(h.Executor.Executed);
        Assert.Contains("Cancelled — variables not filled in.", h.Status);
    }

    /// <summary>What binding buys: a value that reads like a statement is still one value, so a read on a
    /// read-only connection stays a read and runs — the server compares <c>id</c> with that text.</summary>
    [Fact]
    public async Task A_value_that_reads_like_a_write_is_only_a_value()
    {
        var h = NewHarness(readOnly: true);
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["id"] = "1; delete from t" };

        await h.Exec.ExecuteAsync("select * from t where id = :id");

        Assert.StartsWith("select * from t where id = @bearing_id", Assert.Single(h.Executor.Executed));
        Assert.Equal("1; delete from t", Assert.Single(Assert.Single(h.Executor.ExecutedParameters)).Value);
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

    /// <summary>A value left out is never taken for empty text: nothing runs, and the status says how to give
    /// empty text or NULL on purpose.</summary>
    [Fact]
    public async Task A_blank_value_runs_nothing()
    {
        var h = NewHarness();

        await h.Exec.ExecuteAsync("select * from film where film_id = :id");

        Assert.Empty(h.Executor.Executed);
        Assert.Contains(h.Status, s => s.Contains("No value for :id") && s.Contains("null for NULL"));
    }

    [Fact]
    public async Task Null_and_empty_text_are_given_on_purpose()
    {
        var h = NewHarness();
        h.Dialogs.VariablesAnswer = new Dictionary<string, string> { ["gone"] = "null", ["blank"] = "''" };

        await h.Exec.ExecuteAsync("select :gone, :blank");

        Assert.Equal(
            [new QueryParameter("bearing_gone", null, QueryParameterKind.Null),
             new QueryParameter("bearing_blank", "", QueryParameterKind.Text)],
            Assert.Single(h.Executor.ExecutedParameters));
    }
}
