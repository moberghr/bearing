namespace Bearing.Cli.Tools;

/// <summary>
/// What the commands actually do, behind the argument parsing. The seam exists so the whole command
/// surface — dispatch, output shape, exit codes — is drivable from a test with no project file, no keychain
/// and no server.
/// <para>
/// Every method returns an <see cref="ICliResponse"/>: a record per command, serialised once in
/// <see cref="CliRunner"/>. These were hand-built JSON objects first, which made the CLI's public output
/// contract readable only by reading the code that emitted it, and a mistyped key a silent one.
/// </para>
/// </summary>
public interface IBearingHost
{
    /// <summary>The connections the user exposed — name, engine and environment, and nothing that says
    /// where the server is or who it authenticates as.</summary>
    Task<ICliResponse> ListConnectionsAsync(CancellationToken ct);

    /// <summary>Relations in the connection's own database, optionally narrowed to one schema.</summary>
    Task<ICliResponse> ListTablesAsync(string connection, string? schema, CancellationToken ct);

    /// <summary>One relation's columns and the foreign keys touching it.</summary>
    Task<ICliResponse> DescribeTableAsync(string connection, string table, CancellationToken ct);

    /// <summary>Run a read: return its rows, or write them to <see cref="RunRequest.OutPath"/> and report
    /// what was written.</summary>
    Task<ICliResponse> QueryAsync(RunRequest request, CancellationToken ct);

    /// <summary>The query plan as a tree — and with <see cref="RunRequest.Analyze"/>, the measured one.</summary>
    Task<ICliResponse> ExplainAsync(RunRequest request, CancellationToken ct);
}

/// <summary>
/// A command that could not be completed, carrying the sentence whoever ran it should read: an unexposed
/// connection, a refused write, a table that is not there, a server that would not answer.
/// <para>
/// Thrown rather than returned because every one of these is a dead end for that invocation, and caught at
/// exactly one place — <see cref="CliRunner"/>, which prints the message to <b>stderr</b> and exits 1. Both
/// halves matter: a caller reading stdout gets data or nothing, never an explanation it might parse as
/// data, and a script can tell "this failed" from "this returned no rows".
/// </para>
/// </summary>
public sealed class CommandFailure(string message) : Exception(message);
