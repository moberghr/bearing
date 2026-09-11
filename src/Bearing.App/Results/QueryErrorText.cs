using Bearing.Core.Data;

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
        if (error?.SqlState is not { } state) return null;

        if (state == ReadOnlyTransaction)
            return connection is not null && SessionPolicy.IsReadOnly(connection)
                ? $"The server refused this write: {connection.Name} is marked read-only. "
                  + "Turn read-only off for this connection to write to it."
                // Not our setting — the server, the role or an enclosing transaction is read-only. Report
                // that without claiming which, since nothing here checked (§1.1).
                : "The server refused this write: the transaction is read-only.";

        if (state != Cancelled || userCancelled) return null;

        if (connection is not null && SessionPolicy.TimeoutSeconds(connection) > SessionPolicy.NoTimeout)
            return $"The statement ran longer than {connection.Name}'s statement timeout "
                   + $"({SessionPolicy.TimeoutLabel(connection)}) and the server cancelled it. "
                   + "This is a server-side limit on the connection, not Bearing giving up waiting.";

        // No timeout configured and the user did not press Esc: something outside this app cancelled it —
        // pg_cancel_backend, or a timeout set on the server rather than by us. Say that and no more.
        return "The server cancelled this statement. Nothing in Bearing asked it to, so it was cancelled "
               + "on the server — a statement_timeout set there, or someone calling pg_cancel_backend.";
    }
}
