using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Per-tab execution: two editor tabs run at the same time (the old shell-wide single-flight dropped the
/// second run and let Esc cancel the wrong query). Drives <see cref="ExecutionViewModel"/> with fakes +
/// a <see cref="GatedExecutor"/> so runs can be held mid-flight — no live database needed.
/// </summary>
public class ConcurrentExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-conc", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private (WorkspaceContext ctx, GatedExecutor gated) NewContext(out ConnectionInfo conn)
    {
        Directory.CreateDirectory(_root);
        var gated = new GatedExecutor();
        var provider = new FakeProvider { Executor = gated };
        var ctx = new WorkspaceContext(
            provider,
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore());
        conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "c", ProviderId = "postgres", Database = "app" };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        return (ctx, gated);
    }

    [Fact]
    public async Task Two_tabs_run_concurrently_and_cancel_is_per_tab()
    {
        var (ctx, gated) = NewContext(out var conn);
        var t1 = new EditorTabViewModel("t1") { ConnectionId = conn.Id };
        var t2 = new EditorTabViewModel("t2") { ConnectionId = conn.Id };
        ctx.Tabs.Add(t1);
        ctx.Tabs.Add(t2);
        ctx.SelectedTab = t1;

        var exec = new ExecutionViewModel(ctx, dialogs: null);

        // Start a run on t1, switch to t2, start a run on t2 — both left in flight (the gate holds them).
        var run1 = exec.ExecuteAsync("select 1");
        ctx.SelectedTab = t2;
        var run2 = exec.ExecuteAsync("select 2");

        // Both are running at once: the pre-fix global single-flight would have dropped the second run.
        Assert.True(t1.IsRunning);
        Assert.True(t2.IsRunning);
        await WaitUntil(() => gated.Started == 2); // both reached the executor concurrently

        // The Run/Cancel façade tracks the *selected* tab (t2); cancelling hits only t2.
        Assert.True(exec.IsBusy);
        Assert.Equal("Cancel (Esc)", exec.RunButtonText);
        exec.CancelExecution();
        await run2;

        Assert.False(t2.IsRunning); // the cancelled tab stopped …
        Assert.True(t1.IsRunning);  // … the other tab keeps running

        // Releasing t1 lets it finish on its own; its result lands on its own VM.
        gated.Release("select 1");
        await run1;
        Assert.False(t1.IsRunning);
        Assert.NotNull(t1.LastResult);

        // With the selected tab (t2) idle again, the façade reads not-busy.
        Assert.False(exec.IsBusy);
        Assert.Equal("Run (Ctrl+Enter)", exec.RunButtonText);
    }

    [Fact]
    public async Task A_second_run_on_the_same_tab_is_rejected_while_it_is_running()
    {
        var (ctx, gated) = NewContext(out var conn);
        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;

        var exec = new ExecutionViewModel(ctx, dialogs: null);

        var run = exec.ExecuteAsync("select 1");
        await WaitUntil(() => gated.Started == 1);

        // A second run while this tab is busy is a no-op (one operation per tab) — completes immediately.
        await exec.ExecuteAsync("select 2");
        Assert.Equal(1, gated.Started); // the second SQL never reached the executor

        gated.Release("select 1");
        await run;
        Assert.False(tab.IsRunning);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }
}
