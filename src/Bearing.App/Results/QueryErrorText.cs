using Bearing.Core.Data;
using Bearing.Sessions;

namespace Bearing.App.Results;

/// <summary>
/// Explains the two SQLSTATEs the per-connection safety settings produce, in terms of the setting that
/// produced them (#99 / #105). Pure, so it is testable without a server.
/// <para>
/// In the App layer rather than beside <c>PostgresErrorText</c> because attribution needs the
/// <see cref="ConnectionInfo"/>, and the executor does not have one — it is built from a factory. The
/// alternative is matching the server's own message text ("canceling statement due to statement timeout"),
/// which is translated under <c>lc_messages</c> and would silently stop matching on a non-English server.
/// The setting we configured is a fact we hold; the server's wording is not.
/// </para>
/// </summary>
internal static class QueryErrorText
{
    /// <summary>A statement was cancelled — by a timeout, by the user, or by someone else's
    /// <c>pg_cancel_backend</c>.</summary>
    public const string Cancelled = "57014";

    /// <summary>A write reached a read-only transaction. On a connection Bearing marked read-only this is
    /// <see cref="ConnectionInfo.ReadOnly"/> doing its job; on any other it is the server's own setting.</summary>
    public const string ReadOnlyTransaction = "25006";

    /// <summary>
    /// MySQL / MariaDB's read-only refusal (ER_CANT_EXECUTE_IN_READ_ONLY_TRANSACTION). That executor records
    /// the server's error <em>number</em> as <see cref="QueryError.SqlState"/>, as SQL Server's does, so the
    /// number is only read as this on a <c>mysql</c> connection — on SQL Server 1792 means something else.
    /// </summary>
    public const string MySqlReadOnlyTransaction = "1792";

    /// <summary>
    /// MySqlConnector's <c>CommandTimeoutExpired</c> (measured on both engines by
    /// <c>A_statement_timeout_stops_the_statement_and_is_not_reported_as_a_cancel</c>). The timeout is the
    /// driver's own: when it expires it sends <c>KILL QUERY</c> for the statement — the server has no
    /// counterpart of Postgres' <c>statement_timeout</c> that Bearing sets (§1.9b).
    /// </summary>
    public const string MySqlCommandTimeout = "-1";

    /// <summary>ER_QUERY_INTERRUPTED: the statement was killed on the server. The executor turns the user's
    /// own Esc into a cancellation before it gets here, and Bearing's timeout arrives as
    /// <see cref="MySqlCommandTimeout"/>, so what is left was stopped by someone else.</summary>
    public const string MySqlInterrupted = "1317";

    /// <summary>The server's own execution-time limit: MySQL's <c>max_execution_time</c> (3024) and
    /// MariaDB's <c>max_statement_time</c> (1969). Bearing sets neither.</summary>
    public const string MySqlServerTimeLimit = "3024";
    public const string MariaDbServerTimeLimit = "1969";

    /// <summary>
    /// The sentence to show for a failed result, or null to leave the existing text alone. Null is the
    /// common case: this only speaks for the two states a connection setting explains.
    /// </summary>
    /// <param name="userCancelled">
    /// Whether the user asked for this (Esc / the Cancel button). A 57014 is then already reported as their
    /// own cancel and must not be blamed on a timeout — the same distinction the run path already draws by
    /// checking its cancellation token.
    /// </param>
    public static string? Explain(QueryError? error, ConnectionInfo? connection, bool userCancelled = false)
    {
        if (error?.SqlState is not { } state)
        {
            return null;
        }

        var readOnly = state == ReadOnlyTransaction
            || (state == MySqlReadOnlyTransaction && ProviderTraits.For(connection) == ProviderTraits.MySql);
        if (readOnly)
        {
            return connection is not null && SessionPolicy.IsReadOnly(connection)
                ? $"The server refused this write: {connection.Name} is marked read-only. "
                  + "Turn read-only off for this connection to write to it."
                // Not our setting — the server, the role or an enclosing transaction is read-only. Report
                // that without claiming which, since nothing here checked (§1.1).
                : "The server refused this write: the transaction is read-only.";
        }

        if (ProviderTraits.For(connection) == ProviderTraits.MySql)
        {
            return userCancelled ? null : ExplainMySql(state, connection);
        }

        if (state != Cancelled || userCancelled)
        {
            return null;
        }

        if (connection is not null && SessionPolicy.TimeoutSeconds(connection) > SessionPolicy.NoTimeout)
        {
            return $"The statement ran longer than {connection.Name}'s statement timeout "
                   + $"({SessionPolicy.TimeoutLabel(connection)}) and the server cancelled it. "
                   + "This is a server-side limit on the connection, not Bearing giving up waiting.";
        }

        // No timeout configured and the user did not press Esc: something outside this app cancelled it —
        // pg_cancel_backend, or a timeout set on the server rather than by us. Say that and no more.
        return "The server cancelled this statement. Nothing in Bearing asked it to, so it was cancelled "
               + "on the server — a statement_timeout set there, or someone calling pg_cancel_backend.";
    }

    /// <summary>The MySQL / MariaDB side of <see cref="Explain"/>: the same three answers — our timeout, a
    /// limit or kill on the server, everything else left alone — in that engine's terms.</summary>
    private static string? ExplainMySql(string state, ConnectionInfo? connection)
    {
        switch (state)
        {
            case MySqlCommandTimeout when connection is not null
                                           && SessionPolicy.TimeoutSeconds(connection) > SessionPolicy.NoTimeout:
                return $"The statement ran longer than {connection.Name}'s statement timeout "
                       + $"({SessionPolicy.TimeoutLabel(connection)}), so Bearing asked the server to stop it "
                       + "(KILL QUERY).";
            case MySqlCommandTimeout:
                // No timeout set in Bearing, yet the driver's expired: the only other place it comes from is
                // a command timeout in the connection's options.
                return "The driver's command timeout expired and it asked the server to stop the statement "
                       + "(KILL QUERY). No statement timeout is set for this connection in Bearing, so the limit "
                       + "came from a command timeout in its options.";
            case MySqlInterrupted:
                return "The server interrupted this statement. Nothing in Bearing asked it to, so it was stopped "
                       + "on the server — someone running KILL QUERY, for instance.";
            case MySqlServerTimeLimit or MariaDbServerTimeLimit:
                return "The statement ran past the server's own execution-time limit (max_execution_time on "
                       + "MySQL, max_statement_time on MariaDB). Bearing sets neither; it is set on the server "
                       + "or in the statement.";
            default:
                return null;
        }
    }
}
