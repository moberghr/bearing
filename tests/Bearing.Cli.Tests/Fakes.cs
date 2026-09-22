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
    public JsonNode? Answer { get; set; }

    public string? Connection { get; private set; }
    public string? Schema { get; private set; }
    public string? Table { get; private set; }
    public string? Sql { get; private set; }
    public int? MaxRows { get; private set; }
    public List<string> Calls { get; } = [];

    public Task<JsonNode> ListConnectionsAsync(CancellationToken ct) => Respond(Commands.Connections);

    public Task<JsonNode> ListTablesAsync(string connection, string? schema, CancellationToken ct)
    {
        Connection = connection;
        Schema = schema;
        return Respond(Commands.Tables);
    }

    public Task<JsonNode> DescribeTableAsync(string connection, string table, CancellationToken ct)
    {
        Connection = connection;
        Table = table;
        return Respond(Commands.Describe);
    }

    public Task<JsonNode> QueryAsync(string connection, string sql, int? maxRows, CancellationToken ct)
    {
        Connection = connection;
        Sql = sql;
        MaxRows = maxRows;
        return Respond(Commands.Query);
    }

    private Task<JsonNode> Respond(string call)
    {
        Calls.Add(call);
        if (FailWith is { } reason) throw new CommandFailure(reason);
        return Task.FromResult(Answer ?? new JsonObject { ["called"] = call });
    }
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
