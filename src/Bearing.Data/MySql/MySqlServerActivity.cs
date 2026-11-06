using Bearing.Core.Data;
using MySqlConnector;

namespace Bearing.Data.MySql;

/// <summary>
/// The server's sessions and the two actions on one (#101), for MySQL and MariaDB — read from
/// <c>information_schema.processlist</c>, which both engines have, and acted on with <c>KILL QUERY</c>
/// (cancel: the statement stops, the session stays) and <c>KILL CONNECTION</c> (terminate).
/// <para>
/// What the process list does not carry, and so the panel does not show: a session's start time (MySQL
/// thread ids increase monotonically instead, which is what makes the id alone a safe row identity) and a
/// client application name (MySqlConnector sends one as a connection attribute, visible only through
/// <c>performance_schema</c> on MySQL — not on every server, so not relied on). <see cref="BackendActivity.IsOurs"/>
/// is therefore false for every row rather than guessed from a user name.
/// </para>
/// </summary>
public sealed class MySqlServerActivity : IServerActivity
{
    private readonly MySqlConnectionFactory _factory;

    public MySqlServerActivity(MySqlConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Whether this account sees every session or only its own. Without the <c>PROCESS</c> privilege the
    /// process list holds the account's own threads only (§9.13a), and the panel must say so rather than
    /// imply the server is quiet. Read from <c>SHOW GRANTS</c>, which reports what the account holds
    /// directly and through its default roles — the same set the server consults for the process list.
    /// </summary>
    private static async Task<bool> SeesAllSessionsAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("show grants", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var grant = r.GetString(0);
            var on = grant.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
            var privileges = on > 0 ? grant[..on] : grant;
            // Only a global grant (`ON *.*`) can carry PROCESS; SUPER implies it on older servers.
            var global = grant.Contains(" ON *.* ", StringComparison.OrdinalIgnoreCase);
            if (global && (privileges.Contains("ALL PRIVILEGES", StringComparison.OrdinalIgnoreCase)
                           || privileges.Contains("PROCESS", StringComparison.OrdinalIgnoreCase)
                           || privileges.Contains("SUPER", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

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
        var seesAll = await SeesAllSessionsAsync(conn, ct).ConfigureAwait(false);

        var sql = ActivitySql
                  + (filter.Database is null ? "" : "\n  and db = @db")
                  + (filter.IncludeIdle ? "" : "\n  and command <> 'Sleep'")
                  + "\norder by case when command = 'Query' then 0 else 1 end, time desc, id";
        await using var cmd = new MySqlCommand(sql, conn);
        if (filter.Database is not null)
        {
            cmd.Parameters.AddWithValue("@db", filter.Database);
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
            var active = string.Equals(command, "Query", StringComparison.OrdinalIgnoreCase);
            TimeSpan? elapsed = r.IsDBNull(4) ? null : TimeSpan.FromSeconds(Convert.ToInt64(r.GetValue(4)));
            backends.Add(new BackendActivity(
                Pid: (int)id,
                BackendStart: null,
                User: r.IsDBNull(1) ? null : r.GetString(1),
                Database: r.IsDBNull(2) ? null : r.GetString(2),
                Application: null,
                State: StateOf(command),
                WaitEvent: r.IsDBNull(5) || r.GetString(5).Length == 0 ? null : r.GetString(5),
                RunningFor: active ? elapsed : null,
                StateFor: elapsed,
                Query: r.IsDBNull(6) ? null : r.GetString(6),
                IsOurs: false));
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
