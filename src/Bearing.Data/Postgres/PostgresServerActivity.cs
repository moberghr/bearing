using Bearing.Core.Data;
using Npgsql;

namespace Bearing.Data.Postgres;

/// <summary>
/// <c>pg_stat_activity</c>, and the two functions that act on a backend (#101).
/// </summary>
public sealed class PostgresServerActivity : IServerActivity
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresServerActivity(NpgsqlConnectionFactory factory) => _factory = factory;

    /// <summary>What <c>application_name</c> every connection this app opens reports — see
    /// <c>PostgresConnectionString.Build</c>. Matching on it is what marks a row as one of ours.</summary>
    public const string OurApplicationName = "bearing";

    /// <summary>
    /// Whether the connected role can see every session or only its own.
    /// <para>
    /// Asked outright, because the result set cannot answer it. A role without <c>pg_read_all_stats</c> does
    /// not get other sessions' rows with the details blanked — it does not get the rows, so there is nothing
    /// in the list to count or flag. Measured on PostgreSQL 18: with the role, a read returned three client
    /// backends; without it, the same read at the same moment returned one.
    /// </para>
    /// <para>
    /// Two branches because they are two different grants: a superuser sees everything without being a member
    /// of anything, and <c>pg_has_role</c> alone would report such a connection as restricted.
    /// </para>
    /// </summary>
    private const string SeesAllSql = """
        select current_setting('is_superuser')::bool
               or pg_has_role(current_user, 'pg_read_all_stats', 'member')
        """;

    /// <summary>
    /// The sessions themselves.
    /// <para>
    /// <c>backend_type = 'client backend'</c> keeps out the autovacuum launcher, the walwriter and the rest of
    /// the server's own processes: they are always there, they are not anybody's query, and they are not
    /// cancelable. <c>pid &lt;&gt; pg_backend_pid()</c> drops the connection doing the reading — the panel
    /// polling itself is noise, and it is the one row the user can never act on.
    /// </para>
    /// <para>
    /// The elapsed time is computed here rather than from a returned timestamp, so the number the user reads
    /// is the server's own arithmetic and carries no clock difference between two machines.
    /// </para>
    /// </summary>
    private const string ActivitySql = """
        select pid,
               backend_start,
               usename,
               datname,
               application_name,
               state,
               nullif(concat_ws(': ', wait_event_type, wait_event), '') as wait,
               now() - query_start as running_for,
               query
        from pg_stat_activity
        where backend_type = 'client backend'
          and pid <> pg_backend_pid()
        """;

    /// <summary>
    /// The optional narrowing to one database, appended rather than written as
    /// <c>($1 is null or datname = $1)</c>.
    /// <para>
    /// §9.9's trap: a null-valued <em>untyped</em> placeholder gives Postgres nothing to infer a type from and
    /// the read fails — silently, wherever a failed kind becomes an empty list. Building the predicate keeps
    /// the parameter always-valued, and the value is still a parameter rather than interpolated.
    /// </para>
    /// </summary>
    private const string DatabasePredicate = "\n  and datname = $1";

    /// <summary>
    /// Drops backends sitting plainly idle. <c>is distinct from</c> rather than <c>&lt;&gt;</c> so a null state
    /// survives: a row whose state the role may not read is not thereby an idle one, and hiding it would be
    /// this panel asserting something it never checked.
    /// </summary>
    private const string BusyPredicate = "\n  and state is distinct from 'idle'";

    private const string ActivityOrder = "\norder by running_for desc nulls last, pid";

    public async Task<ServerActivity> GetActivityAsync(ActivityFilter filter, CancellationToken ct)
    {
        // One connection, two commands — the shape GetTableDetailsAsync already uses for reads that belong
        // together. Not one query with the flag cross-joined on: this read legitimately returns no rows (a
        // server whose only session is the one asking), and the flag would then have nowhere to ride.
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool seesAll;
        await using (var probe = new NpgsqlCommand(SeesAllSql, conn))
            seesAll = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;

        var backends = new List<BackendActivity>();
        // Narrowed on the server rather than after the fact: on a host with a few hundred pooled connections
        // the difference is what crosses the wire every 2.5 seconds.
        var sql = ActivitySql
                  + (filter.Database is null ? "" : DatabasePredicate)
                  + (filter.IncludeIdle ? "" : BusyPredicate)
                  + ActivityOrder;
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (filter.Database is not null) cmd.Parameters.AddWithValue(filter.Database);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var application = r.IsDBNull(4) ? null : r.GetString(4);
            backends.Add(new BackendActivity(
                Pid: r.GetInt32(0),
                BackendStart: r.IsDBNull(1) ? null : new DateTimeOffset(r.GetDateTime(1)),
                User: r.IsDBNull(2) ? null : r.GetString(2),
                Database: r.IsDBNull(3) ? null : r.GetString(3),
                Application: application,
                State: r.IsDBNull(5) ? null : r.GetString(5),
                WaitEvent: r.IsDBNull(6) ? null : r.GetString(6),
                // Null when the backend is running nothing — idle, not instantaneous.
                RunningFor: r.IsDBNull(7) ? null : r.GetFieldValue<TimeSpan>(7),
                Query: r.IsDBNull(8) ? null : r.GetString(8),
                IsOurs: string.Equals(application, OurApplicationName, StringComparison.Ordinal)));
        }

        return new ServerActivity(backends, seesAll);
    }

    public Task<bool> CancelBackendAsync(int pid, CancellationToken ct)
        => AskAsync("select pg_cancel_backend($1)", pid, ct);

    public Task<bool> TerminateBackendAsync(int pid, CancellationToken ct)
        => AskAsync("select pg_terminate_backend($1)", pid, ct);

    /// <summary>
    /// Run one of the two backend functions and report what the server answered.
    /// <para>
    /// Parameterised, like every other statement this app generates (§5.4) — a pid is a number we were handed
    /// by a previous read rather than typed, but the rule does not have an exception for values we trust.
    /// </para>
    /// <para>
    /// The boolean is returned rather than discarded: both functions answer false for a backend that has
    /// already gone, or one this role may not signal. "Asked, and the server declined" and "asked, and it
    /// worked" are different outcomes and the panel says which.
    /// </para>
    /// </summary>
    private async Task<bool> AskAsync(string sql, int pid, CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(pid);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
    }
}
