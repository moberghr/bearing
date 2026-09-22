using System.Text.Json.Nodes;

namespace Bearing.Cli.Tools;

/// <summary>
/// What the four commands actually do, behind the argument parsing. The seam exists so the whole command
/// surface — dispatch, output shape, exit codes — is drivable from a test with no project file, no keychain
/// and no server.
/// <para>
/// Every method returns a <see cref="JsonNode"/> rather than a typed record: the result is printed to the
/// caller verbatim and nothing in this process computes against it, so a record per command would be a
/// shape maintained in two places for no reader. <c>SchemaObjectInfo</c>'s reasoning, one layer out. The
/// <c>--table</c> rendering reads this same JSON for the same reason (<see cref="TextTable"/>).
/// </para>
/// </summary>
public interface IBearingHost
{
    /// <summary>The connections the user exposed — name, engine and environment, and nothing that says
    /// where the server is or who it authenticates as.</summary>
    Task<JsonNode> ListConnectionsAsync(CancellationToken ct);

    /// <summary>Relations in the connection's own database, optionally narrowed to one schema.</summary>
    Task<JsonNode> ListTablesAsync(string connection, string? schema, CancellationToken ct);

    /// <summary>One relation's columns and the foreign keys touching it.</summary>
    Task<JsonNode> DescribeTableAsync(string connection, string table, CancellationToken ct);

    /// <summary>Run a read and return its rows.</summary>
    Task<JsonNode> QueryAsync(string connection, string sql, int? maxRows, CancellationToken ct);
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
