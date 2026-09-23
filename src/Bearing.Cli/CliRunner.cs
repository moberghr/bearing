using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bearing.Cli.Tools;

namespace Bearing.Cli;

/// <summary>
/// One parsed invocation, against a host, writing to a pair of writers. Separate from <c>Program</c> so the
/// whole command surface — dispatch, output shape, exit codes — is testable without a process, a project
/// file or a server.
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

    /// <summary>
    /// How a response reaches the caller. The one place anything is serialised, which is why the commands
    /// return records: the output shape is declared once, in <c>Responses.cs</c>, rather than spelled out
    /// at every place that emits it.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        // snake_case because that is what the output already promised — `row_count`, `duration_ms` — and it
        // is the CLI's public contract, not a style choice to revisit.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // An absent measurement is absent, not null: a plan that was not analysed has no actual rows, which
        // is a different thing from having none.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

        ICliResponse response;
        try
        {
            response = await InvokeAsync(options, host, ct).ConfigureAwait(false);
        }
        catch (CommandFailure failure)
        {
            // The sentence goes to stderr and the exit code carries the verdict, so a caller that only
            // reads stdout never mistakes an explanation for data.
            await stderr.WriteLineAsync(failure.Message);
            return Failed;
        }

        await stdout.WriteLineAsync(
            options.Format switch
            {
                OutputFormat.Table => TextTable.Render(response).TrimEnd(),
                // Only the final line break: a last row ending in an empty cell ends in a tab, and trimming
                // that would take a column off it.
                OutputFormat.Tsv => TextTable.Render(response, tabs: true).TrimEnd('\r', '\n'),
                _ => Serialize(response),
            });

        return Ok;
    }

    /// <summary>
    /// A response as JSON, <b>by its runtime type</b>. Serializing by the declared one writes an empty
    /// object: <see cref="ICliResponse"/> has no properties, so <c>Serialize(response, …)</c> resolves the
    /// contract from the interface and emits <c>{}</c> — which the tests caught and nothing else would
    /// have, since an empty object parses fine and every "does not contain" assertion passes against it.
    /// </summary>
    public static string Serialize(ICliResponse response)
        => JsonSerializer.Serialize(response, response.GetType(), Json);

    private static async Task<ICliResponse> InvokeAsync(
        CliOptions options, IBearingHost host, CancellationToken ct)
    {
        switch (options.Command)
        {
            case Commands.Connections:
                return await host.ListConnectionsAsync(ct).ConfigureAwait(false);

            case Commands.Tables:
                return await host.ListTablesAsync(options.Arguments[0], options.Schema, ct).ConfigureAwait(false);

            case Commands.Describe:
                return await host.DescribeTableAsync(options.Arguments[0], options.Arguments[1], ct)
                    .ConfigureAwait(false);

            case Commands.Query:
                return await host.QueryAsync(await RequestAsync(options, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);

            case Commands.Explain:
                return await host.ExplainAsync(await RequestAsync(options, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);

            // Unreachable: the parser rejects an unknown command and defaults an empty one to help. Stated
            // rather than left to a default arm, because a command added to the parser and not here would
            // otherwise fail at runtime with nothing saying why.
            default:
                throw new CommandFailure($"'{options.Command}' is not a command bearing can run.");
        }
    }

    /// <summary>The statement to run, and everything asked for around it.</summary>
    private static async Task<RunRequest> RequestAsync(CliOptions options, CancellationToken ct)
        => new(options.Arguments[0], await SqlAsync(options, ct).ConfigureAwait(false))
        {
            MaxRows = options.MaxRows,
            UnlimitedRows = options.UnlimitedRows,
            OutPath = options.Out,
            TimeoutSeconds = options.TimeoutSeconds,
            Analyze = options.Analyze,
        };

    /// <summary>
    /// The SQL: the second argument, or the file <c>--file</c> named, or standard input for <c>-</c>. The
    /// parser has already refused both-or-neither, so exactly one of these holds here.
    /// </summary>
    private static async Task<string> SqlAsync(CliOptions options, CancellationToken ct)
    {
        if (options.File is not { } path) return options.Arguments[1];

        if (path == "-")
            return Whole(await Console.In.ReadToEndAsync(ct).ConfigureAwait(false), "standard input");

        try
        {
            return Whole(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CommandFailure)
        {
            // The path is the caller's, so naming it is most of the diagnosis.
            throw new CommandFailure($"Could not read {path}: {ex.Message}");
        }
    }

    /// <summary>An empty file is refused rather than sent. A statement of nothing is not a query, and the
    /// server's complaint about it would be a worse sentence than this one.</summary>
    private static string Whole(string sql, string source)
        => string.IsNullOrWhiteSpace(sql)
            ? throw new CommandFailure($"There is no SQL in {source}.")
            : sql;
}
