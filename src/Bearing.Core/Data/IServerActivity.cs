namespace Bearing.Core.Data;

/// <summary>
/// One backend on the server this connection points at (#101) — a session, not a statement.
/// </summary>
/// <param name="Pid">The backend's process id, and the handle both actions take.</param>
/// <param name="BackendStart">When the backend connected. Part of the row's identity: a pid alone is reused
/// by the server, and this list refreshes under a pointer that may be aiming at a row.</param>
/// <param name="RunningFor">
/// How long the current statement has been running, <b>as the server measured it</b> — null unless the
/// backend is <c>active</c>.
/// <para>
/// <b>Null for an idle backend is the whole point, and it has to be asked for.</b> <c>query_start</c> keeps
/// the <em>last</em> statement's start after that statement finished, so a plain <c>now() - query_start</c>
/// hands back a growing duration for a session executing nothing — and a backend that has sat
/// <c>idle in transaction</c> for 41 minutes then reads as one that has been running a statement for 41
/// minutes. The server is asked <c>case when state = 'active' then …</c> so the absence is a fact rather than
/// something the caller has to remember to check. What that backend has been doing for 41 minutes is
/// <see cref="StateFor"/>'s answer.
/// </para>
/// <para>
/// A duration rather than a <c>query_start</c> timestamp the client subtracts from, for two reasons: it puts
/// no clock skew between two machines into a number the user reads, and it lets the demo fixture state a
/// duration without a clock (§4.6). Each poll brings a fresh value, so it advances at the poll's cadence and
/// needs no second timer.
/// </para>
/// </param>
/// <param name="StateFor">
/// How long the backend has been in the state it reports — the one duration that means something for every
/// row, and for an <c>active</c> one it is the running time again.
/// <para>
/// This is what makes hiding <see cref="RunningFor"/> from an idle backend affordable rather than a loss:
/// "idle in transaction, for 41 minutes" is the sentence the panel exists to say, and it is a different
/// sentence from "running for 41 minutes". Null when the role may not see the backend's details.
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
    TimeSpan? StateFor,
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
/// What to ask the server for (#101). Both narrowings exist because the honest unfiltered answer is
/// unreadable on a production host: every database, and a connection pool's worth of sessions doing nothing.
/// </summary>
/// <param name="Database">One database, or null for every database on the server.</param>
/// <param name="IncludeIdle">
/// Whether to include backends sitting plainly <c>idle</c>.
/// <para>
/// Off by default. An idle backend is a pooled connection between statements — there is nothing on it to
/// cancel, and on an application server there are hundreds. <b><c>idle in transaction</c> is not idle</b> for
/// this purpose and is never hidden: it holds locks, it is the state worth noticing, and burying it would
/// remove the row the panel most exists to show.
/// </para>
/// </param>
public sealed record ActivityFilter(string? Database, bool IncludeIdle = false)
{
    /// <summary>Everything the server will say, unnarrowed — what a test or an audit wants.</summary>
    public static ActivityFilter Everything { get; } = new(null, IncludeIdle: true);
}

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
    /// <para>
    /// <c>pg_stat_activity</c> is cluster-wide and includes every pooled connection between statements, so the
    /// unfiltered read is every session on the host, most of them with nothing to say. The caller narrows it;
    /// see <see cref="ActivityFilter"/>.
    /// </para>
    /// </summary>
    Task<ServerActivity> GetActivityAsync(ActivityFilter filter, CancellationToken ct);

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
