using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bearing.Cli.Tools;

namespace Bearing.Cli;

/// <summary>
/// One parsed invocation, against a host, writing to a pair of writers. Separate from
/// <c>Program</c> so the whole command surface — dispatch, output shape, exit codes — is testable without
/// a process, a project file or a server.
/// </summary>
public static class CliRunner
{
    public const int Ok = 0;

    /// <summary>The command ran and could not be completed: an unexposed connection, a refused write, a
    /// table that is not there, a server that would not answer.</summary>
    public const int Failed = 1;

    /// <summary>The arguments were wrong. Distinct from <see cref="Failed"/> because a script retrying a
    /// usage error will retry forever.</summary>
    public const int Usage = 2;

    private static readonly JsonSerializerOptions Output = new()
    {
        // Indented here, unlike the payload of a protocol message: this goes to a terminal or into a
        // pipeline where a human will read the failure, and one call's output is not a context budget.
        WriteIndented = true,
        // The default encoder escapes every non-ASCII character into \uXXXX. That is for embedding JSON in
        // HTML; here it would render a column of Croatian names unreadable to the person and the model both.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(
        CliOptions options, IBearingHost host, IAppLauncher app,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (options.LaunchApp)
        {
            // Before the project is resolved and before any connection is touched: opening the window has
            // nothing to do with a project on disk, and a broken one must not stop it.
            if (app.Launch() is { } failure) { await stderr.WriteLineAsync(failure); return Failed; }
            return Ok;
        }

        if (options.Error is { } error)
        {
            await stderr.WriteLineAsync(error);
            await stderr.WriteLineAsync("Run `bearing --help` for what it takes.");
            return Usage;
        }

        if (options.Help) { await stdout.WriteLineAsync(Commands.Help); return Ok; }

        JsonNode result;
        try
        {
            result = await InvokeAsync(options, host, ct).ConfigureAwait(false);
        }
        catch (CommandFailure failure)
        {
            // The sentence goes to stderr and the exit code carries the verdict, so a caller that only
            // reads stdout never mistakes an explanation for data.
            await stderr.WriteLineAsync(failure.Message);
            return Failed;
        }

        await stdout.WriteLineAsync(
            options.Format == OutputFormat.Table
                ? TextTable.Render(result).TrimEnd()
                : result.ToJsonString(Output));

        return Ok;
    }

    private static Task<JsonNode> InvokeAsync(CliOptions options, IBearingHost host, CancellationToken ct)
        => options.Command switch
        {
            Commands.Connections => host.ListConnectionsAsync(ct),
            Commands.Tables => host.ListTablesAsync(options.Arguments[0], options.Schema, ct),
            Commands.Describe => host.DescribeTableAsync(options.Arguments[0], options.Arguments[1], ct),
            Commands.Query => host.QueryAsync(options.Arguments[0], options.Arguments[1], options.MaxRows, ct),
            // Unreachable: the parser rejects an unknown command and defaults an empty one to help. Stated
            // rather than left to a default arm, because a command added to the parser and not here would
            // otherwise fail at runtime with nothing saying why.
            _ => throw new CommandFailure($"'{options.Command}' is not a command bearing can run."),
        };
}
