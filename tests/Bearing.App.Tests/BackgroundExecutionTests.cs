using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Stage E — background execution (docs/background-execution-plan.md). Concurrency and per-tab cancel are
/// covered by <see cref="ConcurrentExecutionTests"/>; this covers the rest of the contract: a run keeps
/// going when its tab leaves the screen, its status text stops hijacking the status bar, it reports through
/// the completion event instead, and the two "you are about to abandon a query" prompts.
/// </summary>
public class BackgroundExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-bg", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private (WorkspaceContext ctx, GatedExecutor gated) NewContext(out ConnectionInfo conn, IDialogService? dialogs = null)
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
            new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "c", ProviderId = "postgres", Database = "app" };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        return (ctx, gated);
    }

    // ---- results and status routing -------------------------------------------------------------

    [Fact]
    public async Task A_run_that_finishes_on_a_background_tab_toasts_and_leaves_the_status_bar_alone()
    {
        var (ctx, gated) = NewContext(out var conn);
        var t1 = new EditorTabViewModel("t1") { ConnectionId = conn.Id };
        var t2 = new EditorTabViewModel("t2") { ConnectionId = conn.Id };
        ctx.Tabs.Add(t1);
        ctx.Tabs.Add(t2);
        ctx.SelectedTab = t1;

        var status = "";
        ctx.Status = s => status = s;
        var exec = new ExecutionViewModel(ctx, dialogs: null);
        var toasts = new List<BackgroundCompletion>();
        exec.BackgroundCompleted += toasts.Add;

        var run = exec.ExecuteAsync("select 1");
        await WaitUntil(() => gated.Started == 1);

        // The user moves on to another tab; whatever t2 puts in the status bar is now what's on screen.
        ctx.SelectedTab = t2;
        status = "t2 is what the user is looking at";

        gated.Release("select 1");
        await run;

        Assert.Equal("t2 is what the user is looking at", status);   // the finished run did not hijack it
        var toast = Assert.Single(toasts);
        Assert.Equal("t1", toast.TabName);
        Assert.True(toast.TabStillOpen);
        Assert.Contains("c", toast.Message);          // the summary leads with the connection name
        Assert.NotNull(t1.LastResult);                // …and the results still landed on their own tab
    }

    [Fact]
    public async Task A_run_on_the_selected_tab_reports_in_the_status_bar_and_raises_no_toast()
    {
        var (ctx, gated) = NewContext(out var conn);
        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;

        var status = "";
        ctx.Status = s => status = s;
        var exec = new ExecutionViewModel(ctx, dialogs: null);
        var toasts = new List<BackgroundCompletion>();
        exec.BackgroundCompleted += toasts.Add;

        var run = exec.ExecuteAsync("select 1");
        await WaitUntil(() => gated.Started == 1);
        gated.Release("select 1");
        await run;

        Assert.Contains("c", status);
        Assert.Empty(toasts);
    }

    [Fact]
    public async Task A_query_survives_its_tab_being_dropped_and_says_the_results_were_discarded()
    {
        // The tab is closed out from under a query that is still going. (A *project* switch no longer looks
        // like this — it parks tabs, so the run still has somewhere to land; see ProjectSwitchTests.)
        var (ctx, gated) = NewContext(out var conn);
        var tab = new EditorTabViewModel("t1") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;

        var exec = new ExecutionViewModel(ctx, dialogs: null);
        var toasts = new List<BackgroundCompletion>();
        exec.BackgroundCompleted += toasts.Add;

        var run = exec.ExecuteAsync("select 1");
        await WaitUntil(() => gated.Started == 1);

        ctx.Tabs.Clear();
        ctx.SelectedTab = null;
        await ctx.Sessions.CloseAllAsync();   // the shutdown sweep — must not kill the running query

        Assert.True(tab.IsRunning);           // still in flight after the switch

        gated.Release("select 1");
        await run;

        var toast = Assert.Single(toasts);
        Assert.Equal("t1", toast.TabName);
        Assert.False(toast.TabStillOpen);     // nothing left to show the rows on
    }

    [Fact]
    public async Task Closing_all_sessions_keeps_a_leased_one_alive_until_its_query_releases_it()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = new ConnectionInfo { Id = Guid.NewGuid(), Name = "c", ProviderId = "postgres", Database = "app" };

        var lease = await mgr.AcquireAsync(info, CancellationToken.None);
        var factory = (FakeFactory)lease.Session.Factory;

        await mgr.CloseAllAsync();

        Assert.Null(mgr.TryGet(SessionKey.For(info)));      // gone from the live map — nothing new attaches to it
        Assert.Equal(0, factory.DisposeCount); // but the running query's connection is still open

        lease.Dispose();
        await WaitUntil(() => factory.DisposeCount == 1); // freed at the last release (disposal is async)
    }

    // ---- the "you're about to abandon a query" prompts -------------------------------------------

    [Fact]
    public async Task Closing_a_tab_mid_query_asks_first_and_cancels_the_run()
    {
        var dialogs = new FakeDialogs();
        var (ctx, gated, workspace, exec) = NewWorkspace(out var conn, dialogs);
        var tab = workspace.NewTab();
        tab.ConnectionId = conn.Id;
        workspace.NewTab();                    // a second tab, so closing the first isn't the last one

        var toasts = new List<BackgroundCompletion>();
        exec.BackgroundCompleted += toasts.Add;

        ctx.SelectedTab = tab;
        var run = exec.ExecuteAsync("select 1");
        await WaitUntil(() => gated.Started == 1);

        Assert.True(await workspace.CloseTabAsync(tab));
        await run;

        Assert.Equal(new string?[] { tab.Header }, dialogs.CancelRunningPrompts);
        Assert.False(tab.IsRunning);
        Assert.DoesNotContain(tab, workspace.Tabs);
        // No toast: the user asked for this cancel one dialog ago and doesn't need telling.
        Assert.Empty(toasts);
    }

    [Fact]
    public async Task Declining_the_prompt_keeps_both_the_query_and_the_tab()
    {
        var dialogs = new FakeDialogs { CancelRunningAnswer = false };
        var (ctx, gated, workspace, exec) = NewWorkspace(out var conn, dialogs);
        var tab = workspace.NewTab();
        tab.ConnectionId = conn.Id;

        ctx.SelectedTab = tab;
        var run = exec.ExecuteAsync("select 1");
        await WaitUntil(() => gated.Started == 1);

        Assert.False(await workspace.CloseTabAsync(tab));
        Assert.True(tab.IsRunning);
        Assert.Contains(tab, workspace.Tabs);
        Assert.Empty(dialogs.ClosePrompts);    // never got as far as asking about unsaved text

        gated.Release("select 1");
        await run;
    }

    [Fact]
    public async Task An_idle_tab_closes_without_the_running_prompt()
    {
        var dialogs = new FakeDialogs();
        var (_, _, workspace, _) = NewWorkspace(out _, dialogs);
        var tab = workspace.NewTab();
        workspace.NewTab();

        Assert.True(await workspace.CloseTabAsync(tab));
        Assert.Empty(dialogs.CancelRunningPrompts);
    }

    private (WorkspaceContext ctx, GatedExecutor gated, WorkspaceViewModel workspace, ExecutionViewModel exec)
        NewWorkspace(out ConnectionInfo conn, IDialogService dialogs)
    {
        var (ctx, gated) = NewContext(out conn, dialogs);
        var connections = new ConnectionsViewModel(ctx);
        var scripts = new ScriptsViewModel(ctx, () => { });
        var workspace = new WorkspaceViewModel(ctx, scripts, connections, dialogs);
        return (ctx, gated, workspace, new ExecutionViewModel(ctx, dialogs));
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
