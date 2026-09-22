using Bearing.Cli.Tools;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Data.SqlServer;
using Bearing.Persistence;
using Bearing.Sessions;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// What an exposed <b>SQL Server</b> connection refuses, and in what words.
/// <para>
/// No server, and none needed: every refusal here happens in <c>BearingHost.PrepareAsync</c>, which runs
/// before <c>ConnectAsync</c>. That ordering is the feature — a statement an exposed connection may not run
/// never reaches a network — and it is what lets the wording be pinned without a container.
/// </para>
/// </summary>
public class TSqlExposureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bearing-tsql-exposure", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<BearingHost> HostAsync()
    {
        var directory = Path.Combine(_root, "project");
        var store = new JsonProjectStore();
        var project = await store.CreateAsync(directory, "Agent", CancellationToken.None);

        project.Manifest.Connections.Add(new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "agent-mssql",
            ProviderId = SqlServerProvider.ProviderId,
            Host = "nowhere.invalid",
            Port = 1433,
            Database = "app",
            User = "reader",
            ExternalAccess = ExternalAccess.ReadOnly,
        });

        await store.SaveAsync(project, CancellationToken.None);

        var providers = new ProviderRegistry();
        var sessions = new ConnectionSessionManager(
            providers, () => null, idleTimeout: null, clock: null, runSweepTimer: false);
        return new BearingHost(store, directory, providers, sessions);
    }

    private async Task<string> RefusalFor(string sql)
    {
        var host = await HostAsync();
        var failure = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest("agent-mssql", sql), CancellationToken.None));
        return failure.Message;
    }

    /// <summary>
    /// The wording §1.11a-bis exists to protect. The write guard's T-SQL verdict is generous by design —
    /// anything whose lead word is not a known read counts as risky, so that it gets confirmed — and this
    /// call site was reading <c>IsRisky</c> to build its sentence. So <c>declare @id int; select …</c>,
    /// ordinary T-SQL for a read, was refused with "so DECLARE will not run": a claim that it writes, which
    /// nobody checked (§1.1).
    /// <para>
    /// It is still refused. The allow-list's own sentence is the true one, and reaching it is the fix.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_read_the_guard_cannot_vouch_for_is_refused_without_calling_it_a_write()
    {
        var refusal = await RefusalFor("declare @id int; select * from Orders");

        Assert.Contains("not a read", refusal);
        Assert.DoesNotContain("will not run", refusal);   // the sentence that names a write
        Assert.DoesNotContain("reads only", refusal);
    }

    /// <summary>A statement the guard really did name as a write keeps the sentence that names it — that
    /// wording is the more useful one when it is true, which is why it exists.</summary>
    [Fact]
    public async Task A_named_write_still_says_which_verb_will_not_run()
    {
        var refusal = await RefusalFor("update Orders set Total = 0");

        Assert.Contains("reads only", refusal);
        Assert.Contains("UPDATE", refusal);
    }

    /// <summary>The separator-less batch §1.11a-bis measured against a live server: one span, leading word
    /// SELECT, and a DROP inside it. The top-level word scan is what names it.</summary>
    [Fact]
    public async Task A_write_hidden_behind_a_leading_read_is_named()
    {
        var refusal = await RefusalFor("select 1 drop table Orders");

        Assert.Contains("DROP", refusal);
    }

    /// <summary>Plans are Postgres' here, and saying so beats sending EXPLAIN to an engine that will answer
    /// with a syntax error for a command the help advertised.</summary>
    [Fact]
    public async Task Explain_says_this_engine_is_not_served_rather_than_sending_it()
    {
        var host = await HostAsync();

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.ExplainAsync(
            new RunRequest("agent-mssql", "select * from Orders"), CancellationToken.None));

        Assert.Contains("query plans", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
