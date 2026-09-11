namespace Bearing.Core.Data;

/// <summary>
/// One backend on the server this connection points at (#101) — a session, not a statement.
/// </summary>
/// <param name="Pid">The backend's process id, and the handle both actions take.</param>
/// <param name="BackendStart">When the backend connected. Part of the row's identity: a pid alone is reused
/// by the server, and this list refreshes under a pointer that may be aiming at a row.</param>
/// <param name="RunningFor">
/// How long the current statement has been running, <b>as the server measured it</b> — null when the backend
/// is not running one.
/// <para>
/// A duration rather than a <c>query_start</c> timestamp the client subtracts from, for two reasons: it puts
/// no clock skew between two machines into a number the user reads, and it lets the demo fixture state a
/// duration without a clock (§4.6). Each poll brings a fresh value, so it advances at the poll's cadence and
/// needs no second timer.
/// </para>
/// </param>
/// <param name="IsOurs">
/// True when this backend reports <c>application_name = 'bearing'</c> — what
/// <c>PostgresConnectionString.Build</c> stamps on every connection this app opens. It says "something
/// Bearing opened", which is usually the row you came here for; it cannot narrow to this pool's own sockets
/// and does not claim to.
/// </param>
public sealed record BackendActivity(
    int Pid,
    DateTimeOffset? BackendStart,
    string? User,
    string? Database,
    string? Application,
    string? State,
    string? WaitEvent,
    TimeSpan? RunningFor,
    string? Query,
    bool IsOurs);

/// <summary>
/// What the server will say about its own sessions, to the role we are connected as.
/// </summary>
/// <param name="Backends">The sessions this role can see, excluding the connection that did the reading.</param>
/// <param name="SeesAllSessions">
/// Whether this role can see <em>every</em> session, or only its own.
/// <para>
/// Carried rather than inferred, and it has to be asked for separately. <b>A role without
/// <c>pg_read_all_stats</c> does not see other sessions as rows with the details blanked out — it does not
/// see them at all</b> (measured on PostgreSQL 18; a non-superuser's read returned one row where the
/// superuser's returned three). So nothing in the result set can be counted or flagged to reveal the
/// absence, and a short list is indistinguishable from a quiet server unless the server is asked outright.
/// </para>
/// <para>
/// This is §1.7's <c>RoleGrants.Visible</c> in a new place: "this server has one session" and "you are not
/// allowed to see the others" are different answers, and rendering the second as the first is the one
/// mistake this panel must not make.
/// </para>
/// </param>
public sealed record ServerActivity(IReadOnlyList<BackendActivity> Backends, bool SeesAllSessions);

/// <summary>
/// Reading the server's sessions, and acting on one (#101).
/// <para>
/// A seam of its own rather than three more members on <see cref="IMetadataReader"/>, which has no mutating
/// method: it is read-only by construction, and a reader that can end someone's session is a different thing.
/// The name is the point — this is the one interface in the app that is allowed to have an effect on a server
/// beyond running the user's own SQL.
/// </para>
/// </summary>
public interface IServerActivity
{
    /// <summary>
    /// The server's client backends, and whether this role could see all of them.
    /// </summary>
    /// <param name="database">
    /// Narrow to one database, or null for every database on the server.
    /// <para>
    /// <c>pg_stat_activity</c> is cluster-wide, so the unfiltered read returns every session on the host —
    /// other databases, other roles, other applications, and the statement each is running. That is the right
    /// answer when hunting a lock holder and the wrong default for a panel you open beside your own work, so
    /// the caller says which it wants.
    /// </para>
    /// </param>
    Task<ServerActivity> GetActivityAsync(string? database, CancellationToken ct);

    /// <summary>
    /// Cancel whatever statement <paramref name="pid"/> is running; the session survives and the client sees
    /// its query fail as cancelled. Returns what the server answered — false means the request was refused or
    /// the backend was already gone, which is not the same as "it worked".
    /// </summary>
    Task<bool> CancelBackendAsync(int pid, CancellationToken ct);

    /// <summary>
    /// End <paramref name="pid"/>'s session outright, dropping its connection. Returns what the server
    /// answered, as <see cref="CancelBackendAsync"/> does.
    /// </summary>
    Task<bool> TerminateBackendAsync(int pid, CancellationToken ct);
}
