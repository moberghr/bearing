using System.Diagnostics;
using System.Text.Json.Nodes;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Sessions;
using Bearing.Sql;

namespace Bearing.Cli.Tools;

/// <summary>
/// The four tools against a real project: its connections, their pools, and their catalogs.
/// <para>
/// <b>The manifest is re-read on every call.</b> A project file is small and a tool call is a human-paced
/// event, so the cost is nothing — and it is what makes the gate revocable: un-expose a connection in
/// Bearing and the next call from an agent that has been running for an hour already sees it gone. A cached
/// manifest would keep serving a connection whose owner had just withdrawn it, which is the one way this
/// gate could fail quietly.
/// </para>
/// </summary>
public sealed class BearingHost(
    IProjectStore projects,
    string projectDirectory,
    IProviderRegistry providers,
    IConnectionSessionManager sessions,
    IQueryLog? log = null) : IBearingHost
{
    public async Task<JsonNode> ListConnectionsAsync(CancellationToken ct)
    {
        var connections = new JsonArray();
        foreach (var saved in await ExposedAsync(ct).ConfigureAwait(false))
        {
            var unavailable = ExternalAccessPolicy.UnavailableReason(saved);
            var entry = new JsonObject
            {
                ["name"] = saved.Name,
                ["engine"] = EngineName(saved),
                // The access level as a sentence rather than the enum's spelling: it is read by a model,
                // and "read-only" is the fact, while "ReadOnly" is our identifier for it.
                ["access"] = "read-only",
                ["available"] = unavailable is null,
            };

            // Nothing about where the server is, who it authenticates as, or what it is called there —
            // §1.11. The environment label is the exception, and deliberately: it is the user's own word for
            // how dangerous this connection is, and a model that knows it is pointed at production behaves
            // better than one that does not.
            if (!string.IsNullOrWhiteSpace(saved.Environment)) entry["environment"] = saved.Environment;
            if (unavailable is not null) entry["unavailable_reason"] = unavailable;

            connections.Add(entry);
        }

        return new JsonObject { ["connections"] = connections };
    }

    public async Task<JsonNode> ListTablesAsync(string connection, string? schema, CancellationToken ct)
    {
        var info = await ResolveAsync(connection, ct).ConfigureAwait(false);
        using var lease = await ConnectAsync(info, ct).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(info, lease.Session, ct).ConfigureAwait(false);

        return SchemaJson.Tables(snapshot, schema);
    }

    public async Task<JsonNode> DescribeTableAsync(string connection, string table, CancellationToken ct)
    {
        var info = await ResolveAsync(connection, ct).ConfigureAwait(false);
        using var lease = await ConnectAsync(info, ct).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(info, lease.Session, ct).ConfigureAwait(false);

        var (schema, name) = SplitQualified(table);
        var found = snapshot.ResolveTable(schema, name)
            ?? throw new CommandFailure(
                $"'{table}' is not a table or view on '{info.Name}'. "
                + $"Run `bearing {Commands.Tables} {info.Name}` to see what is.");

        return SchemaJson.Table(snapshot, found);
    }

    public async Task<JsonNode> QueryAsync(string connection, string sql, int? maxRows, CancellationToken ct)
    {
        var info = await ResolveAsync(connection, ct).ConfigureAwait(false);

        // The connection's own dialect, never a default: the guard's verdict depends on whether it can read
        // this engine at all, and one it cannot read has every statement treated as risky (§1.2).
        var traits = ProviderTraits.For(info);
        var risks = WriteGuard.Describe(traits.Dialect, sql);

        // Refused here rather than at the server, for the reason §1.9 gives: the caller gets a sentence
        // naming the setting instead of a SQLSTATE. On an engine that enforces read-only in the startup
        // packet this is the early half of two; on one that does not it is the whole of it — and the exposed
        // session is built read-only either way, so this is not the only thing standing between a caller and
        // a write.
        //
        // WriteRefusal decides (§2.6 — the verdict is never re-implemented here), but the sentence is this
        // host's own. Its wording ends "Turn read-only off for this connection to write to it", which is
        // advice for the person at the keyboard and wrong for everyone reaching it through here: read-only is
        // not this connection's setting, it is forced on because the connection is exposed, and turning the
        // setting off would change nothing. Telling a caller to do something that cannot work is §1.1's
        // failure in its plainest form.
        if (WriteRefusal.Reason(info, risks) is not null)
        {
            var verbs = risks.Where(r => r.IsRisky).Select(r => r.Label).Distinct(StringComparer.Ordinal).ToList();
            throw Refused(
                info, sql,
                $"'{info.Name}' is exposed to external tools for reads only, so {string.Join(", ", verbs)} "
                + "will not run. Its owner can widen that in Bearing, or run the statement there themselves.");
        }

        // And then the allow-list, which is the guard that actually holds. WriteRefusal above is a
        // deny-list reading a server setting, and both halves of that were measured to be defeatable from
        // here: `begin read write; select nextval('s')` ran a write on a connection exposed read-only,
        // because neither BEGIN nor SET is a risky verb and the session GUC only governs the *next*
        // transaction. Nothing but an allow-list closes a hole shaped like "a statement nobody listed".
        //
        // Second rather than first only for the wording: an outright write deserves the sentence above,
        // which names the verb. Everything this catches would otherwise be reported as a server error, or
        // not caught at all.
        if (ExternalSqlPolicy.Refuse(traits.Dialect, sql) is { } notARead)
            throw Refused(info, sql, $"{info.Name}: {notARead}");

        using var lease = await ConnectAsync(info, ct).ConfigureAwait(false);

        IReadOnlyList<QueryResult> results;
        var wall = Stopwatch.StartNew();
        try
        {
            results = await lease.Session.Executor
                .ExecuteAsync(sql, new QueryOptions { MaxRows = maxRows ?? Commands.DefaultMaxRows }, ct)
                .ConfigureAwait(false);
            wall.Stop();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CommandFailure)
        {
            throw new CommandFailure($"The query could not be run on '{info.Name}': {SafeErrorText.Of(ex)}");
        }

        Log(info, sql, results, wall.Elapsed);

        if (results.FirstOrDefault(r => !r.Success)?.Error is { } error)
            throw new CommandFailure($"{info.Name}: {error.Message}");

        var sets = new JsonArray();
        foreach (var result in results) sets.Add(Set(result));

        // One shape whatever the SQL was. A single statement is the overwhelming case, but a result that is
        // sometimes an object and sometimes a list is a shape a consumer has to branch on, and the branch it
        // is least likely to have written is the rare one.
        return new JsonObject { ["results"] = sets };
    }

    private static JsonObject Set(QueryResult result)
    {
        var set = ResultJson.Of(result);
        // A statement that returned no grid still said something ("SELECT 0", an affected-row count). It is
        // the only answer such a statement has, so dropping it would report success with nothing in it.
        if (!string.IsNullOrWhiteSpace(result.Message)) set["message"] = result.Message;
        return set;
    }

    // ---- the record -----------------------------------------------------------------------------

    /// <summary>
    /// A refusal, recorded and then thrown. **Refusals are logged, not only executions** — §1.3 calls the
    /// log "the record of SQL the user ran", and a statement refused before it reached the server did not
    /// run. But the reason this path exists at all is that the caller is not the person at the keyboard,
    /// and "an agent tried to drop a table and was stopped" is the single most useful line the log can
    /// hold. It is written with <c>Success = false</c> and the refusal as the error, which is the shape a
    /// failed execution already has.
    /// </summary>
    private CommandFailure Refused(ConnectionInfo info, string sql, string reason)
    {
        Write(info, sql, TimeSpan.Zero, rows: 0, success: false, error: reason);
        return new CommandFailure(reason);
    }

    private void Log(ConnectionInfo info, string sql, IReadOnlyList<QueryResult> results, TimeSpan elapsed)
        => Write(
            info, sql, elapsed,
            results.Sum(r => r.RowCount),
            results.All(r => r.Success),
            results.FirstOrDefault(r => !r.Success)?.Error?.Message);

    private void Write(
        ConnectionInfo info, string sql, TimeSpan duration, long rows, bool success, string? error)
    {
        // Best-effort, like every other use of the log (§5.2): a history that cannot be written must not
        // cost the caller their result. The app's own path swallows here too.
        try
        {
            log?.Append(new QueryLogEntry
            {
                ExecutedAt = DateTimeOffset.UtcNow,
                ProviderId = info.ProviderId,
                ConnectionName = info.Name,
                ConnectionId = info.Id,
                Environment = string.IsNullOrWhiteSpace(info.Environment) ? null : info.Environment,
                Database = info.Database,
                SqlText = sql,
                Duration = duration,
                RowCount = rows,
                Success = success,
                ErrorMessage = error,
                // What makes the row tellable from one the user ran themselves.
                Origin = QueryOrigin.Cli,
            });
        }
        catch { /* the record is not worth the answer */ }
    }

    // ---- resolution ---------------------------------------------------------------------------

    private async Task<IReadOnlyList<ConnectionInfo>> ExposedAsync(CancellationToken ct)
    {
        Project project;
        try
        {
            project = await projects.OpenAsync(projectDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CommandFailure($"The Bearing project could not be read: {SafeErrorText.Of(ex)}");
        }

        return project.Manifest.Connections.Where(ExternalAccessPolicy.IsExposed).ToList();
    }

    /// <summary>
    /// The connection a call named, as an external host must open it — or a sentence saying why it cannot.
    /// <para>
    /// A name that matches a connection the user has <i>not</i> exposed is told so plainly rather than
    /// reported as missing. Hiding it would be theatre: a caller that can run this process can read the same
    /// <c>project.json</c> it reads. What the message buys is a caller that can say "ask the owner to expose
    /// staging" instead of insisting the connection does not exist.
    /// </para>
    /// </summary>
    private async Task<ConnectionInfo> ResolveAsync(string name, CancellationToken ct)
    {
        Project project;
        try
        {
            project = await projects.OpenAsync(projectDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CommandFailure($"The Bearing project could not be read: {SafeErrorText.Of(ex)}");
        }

        var matches = project.Manifest.Connections
            .Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new CommandFailure(
                $"There is no connection called '{name}'. "
                + $"Run `bearing {Commands.Connections}` for the ones there are.");

        // Two connections may share a name — nothing stops it, and the panel tells them apart by folder.
        // Picking one would be picking a server, so it says so instead.
        if (matches.Count > 1)
            throw new CommandFailure($"'{name}' matches more than one connection in this project, so it is ambiguous.");

        var saved = matches[0];
        if (ExternalAccessPolicy.ForExternalHost(saved) is not { } exposed)
            throw new CommandFailure(
                $"'{saved.Name}' is not exposed to external tools. Its owner can allow it in Bearing's "
                + "connection settings.");

        if (ExternalAccessPolicy.UnavailableReason(exposed) is { } unavailable)
            throw new CommandFailure(unavailable);

        return exposed;
    }

    private async Task<SessionLease> ConnectAsync(ConnectionInfo info, CancellationToken ct)
    {
        try
        {
            return await sessions.AcquireAsync(info, ct).ConfigureAwait(false);
        }
        catch (ConnectionFailedException ex)
        {
            // Already a sentence written for a person — the resolver and the factory both phrase these.
            throw new CommandFailure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CommandFailure($"Could not connect to '{info.Name}': {SafeErrorText.Of(ex)}");
        }
    }

    private async Task<ISchemaSnapshot> SnapshotAsync(
        ConnectionInfo info, ConnectionSession session, CancellationToken ct)
    {
        ISchemaSnapshot? snapshot;
        try
        {
            snapshot = await sessions.EnsureSchemaAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CommandFailure($"The catalog of '{info.Name}' could not be read: {SafeErrorText.Of(ex)}");
        }

        return snapshot ?? throw new CommandFailure($"The catalog of '{info.Name}' could not be read.");
    }

    private string EngineName(ConnectionInfo info)
    {
        try { return providers.Get(info.ProviderId).DisplayName; }
        // A project naming an engine this build does not have. The id is still the truest thing we can say
        // about it, and it is not a reason to fail the whole listing.
        catch (Exception) { return info.ProviderId; }
    }

    /// <summary>Split <c>schema.table</c>, leaving a bare name unqualified so the snapshot resolves it
    /// through the search path the way the editor would.</summary>
    internal static (string? Schema, string Name) SplitQualified(string table)
    {
        var trimmed = table.Trim().Trim('"');
        var dot = trimmed.LastIndexOf('.');
        return dot <= 0 || dot == trimmed.Length - 1
            ? (null, trimmed)
            : (trimmed[..dot].Trim('"'), trimmed[(dot + 1)..].Trim('"'));
    }
}
