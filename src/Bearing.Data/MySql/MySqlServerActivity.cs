using System.Runtime.CompilerServices;
using Bearing.Core.Data;
using MySqlConnector;

namespace Bearing.Data.MySql;

/// <summary>
/// The server's sessions and the two actions on one (#101), for MySQL and MariaDB — read from
/// <c>information_schema.processlist</c>, which both engines have, and acted on with <c>KILL QUERY</c>
/// (cancel: the statement stops, the session stays) and <c>KILL CONNECTION</c> (terminate).
/// <para>
/// What the process list does not carry: a session's start time (MySQL thread ids increase monotonically
/// instead, which is what makes the id alone a safe row identity) and a client application name. The second
/// is the <c>program_name</c> connection attribute — MySqlConnector sends <c>ApplicationName</c> there, and
/// <see cref="MySqlConnectionFactory"/> sets it to <c>bearing</c> as Postgres sets <c>application_name</c> —
/// readable from <c>performance_schema.session_connect_attrs</c> only where <c>performance_schema</c> is on
/// (MariaDB ships with it off) and the account may select from it (measured: an account without a grant on
/// it is refused, 1142, on both engines). Where it cannot be read, <see cref="BackendActivity.IsOurs"/> stays
/// false and <see cref="BackendActivity.Application"/> null — "not known", never a guess from a user name.
/// </para>
/// </summary>
public sealed class MySqlServerActivity : IServerActivity
{
    /// <summary>What <see cref="MySqlConnectionFactory"/> sends as <c>program_name</c> on every connection.</summary>
    internal const string OurApplicationName = "bearing";

    private readonly MySqlConnectionFactory _factory;

    /// <summary>
    /// Set once the attribute table has refused us (absent, disabled, or not granted), so a panel polling every
    /// 2.5 seconds does not send a statement known to fail on each tick. Per instance, which is per session.
    /// </summary>
    private volatile bool _attributesUnavailable;

    /// <summary>Set once <c>information_schema.innodb_trx</c> has refused us; cleared whenever visibility is
    /// re-asked (<see cref="ActivityFilter.RecheckVisibility"/>), since a grant may have changed with it.</summary>
    private volatile bool _transactionsUnavailable;

    /// <summary>
    /// The last <see cref="SeesAllSessionsAsync"/> answer, boxed so "not asked yet" (null field) stays distinct
    /// from "asked, could not find out" (a box holding null). Re-asked when the caller says so
    /// (<see cref="ActivityFilter.RecheckVisibility"/>): one or two statements and a grants parse are not worth
    /// sending on every poll tick.
    /// </summary>
    private volatile StrongBox<bool?>? _seesAll;

    public MySqlServerActivity(MySqlConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Whether this account sees every session, only its own, or — null — whether that could not be found out.
    /// <para>
    /// Without the <c>PROCESS</c> privilege the process list holds the account's own threads only (§9.13a),
    /// and the panel must say so rather than imply the server is quiet. Read from the account's grants
    /// <em>including its active roles'</em>, because that is the set the server consults. Measured on MySQL 8.4
    /// and MariaDB 11.4 with <c>PROCESS</c> granted to a role: visible only once the role is active (a default
    /// role); <c>SUPER</c> alone does <em>not</em> reveal other sessions on either engine, so it does not count.
    /// </para>
    /// <list type="bullet">
    /// <item>MySQL: <c>SHOW GRANTS FOR CURRENT_USER() USING &lt;current_role()&gt;</c> — the form that is
    /// documented to fold the active roles' privileges in.</item>
    /// <item>MariaDB: plain <c>SHOW GRANTS</c>, which lists the current role's grants after the user's own.</item>
    /// </list>
    /// A refused read, or one that returned nothing, is null rather than false: "restricted" is a claim about
    /// this account that would then rest on nothing (§1.1).
    /// <para>
    /// <c>Settled</c> says whether the answer may be reused until the next recheck. A read that failed for a
    /// reason of this one try (a dropped connection, a lock wait) is not: caching its null would leave every
    /// tick saying "couldn't check" until the user refreshed — the latch <see cref="ProgramNamesAsync"/>
    /// declines for the same reason.
    /// </para>
    /// </summary>
    private static async Task<(bool? Answer, bool Settled)> SeesAllSessionsAsync(MySqlConnection conn, CancellationToken ct)
    {
        try
        {
            var sql = "show grants";
            var flavor = MySqlServerFlavor.Parse(conn.ServerVersion);
            // Roles, and current_role(), are MySQL 8.0's. On 5.7 the function does not exist (1305) and there
            // are no roles to fold in, so the plain form is the whole answer there.
            if (!flavor.IsMariaDb && flavor.Major >= 8)
            {
                await using var roles = new MySqlCommand("select current_role()", conn);
                var active = await roles.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if (active is not null && QuotedRoleList.IsMatch(active))
                {
                    sql = "show grants for current_user() using " + active;
                }
                else if (active is not null && !string.Equals(active, "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    // Active roles in a shape we will not splice into a statement: their grants cannot be read.
                    return (null, true);
                }
            }

            var any = false;
            await using var cmd = new MySqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                any = true;
                if (GrantsProcess(r.GetString(0)))
                {
                    return (true, true);
                }
            }
            return (any ? false : null, true);
        }
        catch (MySqlException e) when (!ct.IsCancellationRequested)
        {
            // Not a read the panel's deadline cut short. A refusal (or a server with no such statement) is an
            // answer about this account and is kept until the next recheck; anything else is this try's failure
            // and is asked again on the next read.
            return (null, IsUnavailable(e) || (int)e.ErrorCode == 1305);
        }
    }

    /// <summary>
    /// <c>current_role()</c>'s answer when roles are active: the server's own backtick-quoted
    /// <c>`role`@`host`</c> list. Matched whole before it is spliced into <c>USING</c>, so nothing but quoted
    /// identifiers (embedded backticks doubled) ever reaches the statement.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex QuotedRoleList = new(
        "^`(?:[^`]|``)*`@`(?:[^`]|``)*`(?:,`(?:[^`]|``)*`@`(?:[^`]|``)*`)*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True for a global (<c>ON *.*</c>) grant whose privilege list names <c>PROCESS</c> or
    /// <c>ALL [PRIVILEGES]</c>. Privileges are compared as whole list entries, not substrings.
    /// </summary>
    internal static bool GrantsProcess(string grant)
    {
        const string prefix = "GRANT ";
        if (!grant.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var on = grant.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
        if (on < 0 || !grant.AsSpan(on).StartsWith(" ON *.* ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (var privilege in grant[prefix.Length..on].Split(','))
        {
            var p = privilege.Trim();
            if (p.Equals("PROCESS", StringComparison.OrdinalIgnoreCase)
                || p.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                || p.Equals("ALL PRIVILEGES", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Each visible session's <c>program_name</c>, by thread id — empty when the attribute table cannot be
    /// read (see the class summary). Best-effort: a refusal is remembered and nothing is claimed.
    /// </summary>
    private async Task<Dictionary<long, string>> ProgramNamesAsync(MySqlConnection conn, CancellationToken ct)
    {
        var names = new Dictionary<long, string>();
        if (_attributesUnavailable)
        {
            return names;
        }
        try
        {
            await using var cmd = new MySqlCommand(
                "select processlist_id, attr_value from performance_schema.session_connect_attrs"
                + " where attr_name = 'program_name'", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!r.IsDBNull(0) && !r.IsDBNull(1))
                {
                    names[Convert.ToInt64(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)] = r.GetString(1);
                }
            }
        }
        catch (MySqlException e) when (!ct.IsCancellationRequested)
        {
            // 1142 (not granted), 1146 (no such table), or performance_schema off: nothing to read here, and
            // remembered. Anything else — a dropped connection, a lock wait — says nothing about the table, and
            // latching on it would leave every row of this session "not known to be ours" from one bad tick on.
            if (IsUnavailable(e))
            {
                _attributesUnavailable = true;
            }
            names.Clear();
        }
        return names;
    }

    /// <summary>
    /// The thread ids holding an open InnoDB transaction — what turns a <c>Sleep</c> row into "idle in
    /// transaction", the state <see cref="ActivityFilter.IncludeIdle"/> promises never to hide. The process
    /// list alone cannot say it: a session between statements inside a transaction reports <c>Sleep</c> like
    /// any pooled connection. <c>information_schema.innodb_trx</c> needs <c>PROCESS</c> on both engines, so
    /// it is not asked when the account is known not to hold it, and a refusal is empty (§1.1: nothing
    /// claimed) — those sessions then read as idle, as the process list reports them.
    /// </summary>
    private async Task<HashSet<long>> ThreadsInTransactionAsync(
        MySqlConnection conn, bool? seesAll, CancellationToken ct)
    {
        var ids = new HashSet<long>();
        if (seesAll == false || _transactionsUnavailable)
        {
            return ids;
        }
        try
        {
            await using var cmd = new MySqlCommand("select trx_mysql_thread_id from information_schema.innodb_trx", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!r.IsDBNull(0))
                {
                    ids.Add(Convert.ToInt64(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        catch (MySqlException e) when (!ct.IsCancellationRequested)
        {
            // A refusal is remembered until visibility is next re-checked, as the attribute read's is, so a
            // panel whose grants could not be read does not send a statement known to fail on every tick.
            if (IsUnavailable(e))
            {
                _transactionsUnavailable = true;
            }
            ids.Clear();
        }
        return ids;
    }

    /// <summary>The read was refused or has nothing to read from — not granted (1142, 1044, 1227: PROCESS
    /// required), or the table or schema is absent (1146, 1049) — as opposed to a failure of this one try.</summary>
    private static bool IsUnavailable(MySqlException e) => (int)e.ErrorCode is 1044 or 1049 or 1142 or 1146 or 1227;

    private const string ActivitySql = """
        select id, user, db, command, time, state, info
        from information_schema.processlist
        where id <> connection_id()
          and command <> 'Daemon'
          and user not in ('system user', 'event_scheduler')
        """;

    public async Task<ServerActivity> GetActivityAsync(ActivityFilter filter, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var known = _seesAll;
        if (filter.RecheckVisibility || known is null)
        {
            var (answer, settled) = await SeesAllSessionsAsync(conn, ct).ConfigureAwait(false);
            known = new StrongBox<bool?>(answer);
            _seesAll = settled ? known : null;
            _transactionsUnavailable = false;
        }
        var seesAll = known.Value;
        var programs = await ProgramNamesAsync(conn, ct).ConfigureAwait(false);
        var inTransaction = await ThreadsInTransactionAsync(conn, seesAll, ct).ConfigureAwait(false);

        // Idle rows are dropped, except a sleeping one inside a transaction. On the server when no session is in
        // one; otherwise here, after the read — the in-transaction ids are never written into the statement, so
        // its text stays one of a fixed few per filter and the prepared statements a pooled connection caches
        // stay bounded too (a new id list per tick would be a new server-side statement per tick).
        var dropIdleHere = !filter.IncludeIdle && inTransaction.Count > 0;
        var sql = ActivitySql
                  + (filter.Database is null ? "" : "\n  and db = @db")
                  + (filter.IncludeIdle || dropIdleHere ? "" : "\n  and command <> 'Sleep'")
                  + "\norder by case when command = 'Query' then 0 else 1 end, time desc, id";
        await using var cmd = new MySqlCommand(sql, conn);
        if (filter.Database is not null)
        {
            cmd.Parameters.AddWithValue("@db", filter.Database);
            // Bound, not inlined: the driver's escaping ignores a server's NO_BACKSLASH_ESCAPES.
            await cmd.PrepareAsync(ct).ConfigureAwait(false);
        }

        var backends = new List<BackendActivity>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            // Thread ids are unsigned 64-bit; a server that has run long enough to pass int.MaxValue threads
            // is not one this panel can act on, so those rows are skipped rather than truncated into some
            // other session's id.
            var id = Convert.ToInt64(r.GetValue(0));
            if (id > int.MaxValue)
            {
                continue;
            }

            var command = r.IsDBNull(3) ? null : r.GetString(3);
            var sleeping = string.Equals(command, "Sleep", StringComparison.OrdinalIgnoreCase);
            if (dropIdleHere && sleeping && !inTransaction.Contains(id))
            {
                continue;
            }
            var active = string.Equals(command, "Query", StringComparison.OrdinalIgnoreCase);
            TimeSpan? elapsed = r.IsDBNull(4) ? null : TimeSpan.FromSeconds(Convert.ToInt64(r.GetValue(4)));
            var application = programs.GetValueOrDefault(id);
            backends.Add(new BackendActivity(
                Pid: (int)id,
                BackendStart: null,
                User: r.IsDBNull(1) ? null : r.GetString(1),
                Database: r.IsDBNull(2) ? null : r.GetString(2),
                Application: application,
                State: sleeping && inTransaction.Contains(id)
                    ? "idle in transaction"
                    : StateOf(command),
                WaitEvent: r.IsDBNull(5) || r.GetString(5).Length == 0 ? null : r.GetString(5),
                RunningFor: active ? elapsed : null,
                StateFor: elapsed,
                Query: r.IsDBNull(6) ? null : r.GetString(6),
                IsOurs: string.Equals(application, OurApplicationName, StringComparison.Ordinal)));
        }

        return new ServerActivity(backends, seesAll);
    }

    /// <summary>The process list's <c>command</c> in the panel's vocabulary: a sleeping thread is idle and a
    /// running one active, as Postgres words them; anything else (a replication dump, a <c>Connect</c>) is
    /// shown as the server names it.</summary>
    private static string? StateOf(string? command) => command switch
    {
        null => null,
        "Sleep" => "idle",
        "Query" => "active",
        _ => command.ToLowerInvariant(),
    };

    public Task<bool> CancelBackendAsync(int pid, CancellationToken ct) => KillAsync("kill query ", pid, ct);

    public Task<bool> TerminateBackendAsync(int pid, CancellationToken ct) => KillAsync("kill connection ", pid, ct);

    /// <summary>
    /// <c>KILL</c> takes a literal thread id, not a parameter — the id is an <see cref="int"/>, so
    /// interpolating it is injection-safe. False when the thread is already gone (1094, "unknown thread"),
    /// which is the same "nothing was signalled" answer <c>pg_cancel_backend</c> gives; a permission refusal
    /// (1095) propagates, because it is a reason the user needs to read.
    /// </summary>
    private async Task<bool> KillAsync(string verb, int pid, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(verb + pid.ToString(System.Globalization.CultureInfo.InvariantCulture), conn);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (MySqlException ex) when ((int)ex.ErrorCode == 1094)
        {
            return false;
        }
    }
}
