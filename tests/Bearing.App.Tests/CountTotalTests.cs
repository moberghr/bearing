using System;
using System.IO;
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
/// The [Count] action's two outcomes, which used to be one. <c>CountAsync</c> returned null for every
/// failure, so a count that broke (connection gone, table dropped, timeout) was reported as
/// "Count unavailable for this query" — indistinguishable from a query that genuinely can't be counted.
/// Driven with <see cref="PageableExecutor"/>; no live database needed.
/// </summary>
public class CountTotalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-count", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private (WorkspaceContext ctx, PageableExecutor exec) NewContext(out ConnectionInfo conn)
    {
        Directory.CreateDirectory(_root);
        var executor = new PageableExecutor();
        var provider = new FakeProvider { Executor = executor };
        var ctx = new WorkspaceContext(
            provider,
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "c", ProviderId = "postgres", Database = "app" };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        return (ctx, executor);
    }

    /// <summary>Run one statement so a pageable result set (and a live session to lease) exists.</summary>
    private static async Task<(ExecutionViewModel exec, ResultSetViewModel rs)> RunOnce(
        WorkspaceContext ctx, ConnectionInfo conn, Action<string> status)
    {
        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        ctx.Status = status;

        var exec = new ExecutionViewModel(ctx, dialogs: null);
        await exec.ExecuteAsync("select n from t");
        var rs = tab.LastResult!;
        Assert.True(rs.IsPageable);
        Assert.True(rs.CanCount);
        return (exec, rs);
    }

    [Fact]
    public async Task A_successful_count_fills_in_the_total_and_retires_the_action()
    {
        var (ctx, executor) = NewContext(out var conn);
        var status = "";
        var (exec, rs) = await RunOnce(ctx, conn, s => status = s);
        executor.CountValue = 4200;

        await exec.CountTotalAsync(rs);

        Assert.Equal(4200, rs.TotalCount);
        Assert.False(rs.CanCount);
        Assert.Contains("Counted total", status);
    }

    [Fact]
    public async Task An_uncountable_query_reports_unavailable()
    {
        var (ctx, executor) = NewContext(out var conn);
        var status = "";
        var (exec, rs) = await RunOnce(ctx, conn, s => status = s);
        executor.CountValue = null; // the shape can't be wrapped — not a failure

        await exec.CountTotalAsync(rs);

        Assert.Null(rs.TotalCount);
        Assert.Contains("unavailable", status);
        Assert.DoesNotContain("failed", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_count_says_so_and_leaves_the_action_available_to_retry()
    {
        var (ctx, executor) = NewContext(out var conn);
        var status = "";
        var (exec, rs) = await RunOnce(ctx, conn, s => status = s);
        executor.CountError = new InvalidOperationException("connection was closed");

        await exec.CountTotalAsync(rs);

        // The failure is named, not disguised as an uncountable query …
        Assert.Contains("Count failed", status);
        Assert.Contains("connection was closed", status);
        Assert.DoesNotContain("unavailable", status);
        // … and because no total was recorded, [Count] is still offered.
        Assert.Null(rs.TotalCount);
        Assert.True(rs.CanCount);

        // Retrying after the transient failure clears succeeds.
        executor.CountError = null;
        executor.CountValue = 7;
        await exec.CountTotalAsync(rs);
        Assert.Equal(7, rs.TotalCount);
    }
}
