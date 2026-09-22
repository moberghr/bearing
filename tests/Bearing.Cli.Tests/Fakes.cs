using System.Text.Json.Nodes;
using Bearing.Cli;
using Bearing.Cli.Tools;
using Bearing.Core.Logging;

namespace Bearing.Cli.Tests;

/// <summary>
/// A host that records what it was asked and answers with whatever the test set — so the command surface
/// can be driven end to end with no project, no keychain and no server. Hand-rolled, per §4.1.
/// </summary>
internal sealed class RecordingHost : IBearingHost
{
    /// <summary>Set to make every command fail with this sentence, which is how a refusal is simulated.</summary>
    public string? FailWith { get; set; }

    /// <summary>Returned in place of the default answer, for the output-rendering tests.</summary>
    public ICliResponse? Answer { get; set; }

    public RunRequest? Request { get; private set; }
    public string? Connection { get; private set; }
    public string? Schema { get; private set; }
    public string? Table { get; private set; }
    public List<string> Calls { get; } = [];

    public string? Sql => Request?.Sql;
    public int? MaxRows => Request?.MaxRows;

    public Task<ICliResponse> ListConnectionsAsync(CancellationToken ct) => Respond(Commands.Connections);

    public Task<ICliResponse> ListTablesAsync(string connection, string? schema, CancellationToken ct)
    {
        Connection = connection;
        Schema = schema;
        return Respond(Commands.Tables);
    }

    public Task<ICliResponse> DescribeTableAsync(string connection, string table, CancellationToken ct)
    {
        Connection = connection;
        Table = table;
        return Respond(Commands.Describe);
    }

    public Task<ICliResponse> QueryAsync(RunRequest request, CancellationToken ct)
    {
        Request = request;
        Connection = request.Connection;
        return Respond(Commands.Query);
    }

    public Task<ICliResponse> ExplainAsync(RunRequest request, CancellationToken ct)
    {
        Request = request;
        Connection = request.Connection;
        return Respond(Commands.Explain);
    }

    private Task<ICliResponse> Respond(string call)
    {
        Calls.Add(call);
        if (FailWith is { } reason) throw new CommandFailure(reason);
        return Task.FromResult(Answer ?? Called(call));
    }

    /// <summary>The default answer: a one-row result naming the command, so a test that does not care what
    /// came back can still assert that something did.</summary>
    private static ICliResponse Called(string call) => new QueryResponse(
    [
        new ResultSet(
            [new ColumnHeader("called", "text")],
            [new object?[] { call }],
            RowCount: 1,
            Truncated: false,
            DurationMs: 0),
    ]);
}

/// <summary>Stands in for opening the window, and records whether it was asked to.</summary>
internal sealed class FakeAppLauncher : IAppLauncher
{
    /// <summary>Set to make the launch fail with this sentence.</summary>
    public string? FailWith { get; set; }

    public int Launches { get; private set; }

    public string? Launch()
    {
        Launches++;
        return FailWith;
    }
}

/// <summary>One whole invocation: parse, run, and hand back what the user would have seen.</summary>
internal sealed record Invocation(int ExitCode, string Out, string Error);

internal static class Cli
{
    public static Task<Invocation> RunAsync(IBearingHost host, params string[] args)
        => RunAsync(host, new FakeAppLauncher(), args);

    public static async Task<Invocation> RunAsync(IBearingHost host, FakeAppLauncher app, params string[] args)
    {
        await using var stdout = new StringWriter { NewLine = "\n" };
        await using var stderr = new StringWriter { NewLine = "\n" };

        var exit = await CliRunner.RunAsync(
            CliParser.Parse(args), host, app, stdout, stderr, CancellationToken.None);

        return new Invocation(exit, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>Collects what was appended, so a test can assert the record a command left behind.</summary>
internal sealed class RecordingQueryLog : IQueryLog
{
    public List<QueryLogEntry> Entries { get; } = [];

    public void Append(QueryLogEntry entry)
    {
        Entries.Add(entry);
        Appended?.Invoke(entry);
    }

    public event Action<QueryLogEntry>? Appended;

    public Task<IReadOnlyList<QueryLogEntry>> SearchAsync(QueryLogQuery query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryLogEntry>>(Entries);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
