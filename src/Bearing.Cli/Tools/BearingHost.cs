using System.Diagnostics;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Sessions;
using Bearing.Results;
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
    public async Task<ICliResponse> ListConnectionsAsync(CancellationToken ct)
    {
        var connections = new List<ExposedConnection>();
        foreach (var saved in await ExposedAsync(ct).ConfigureAwait(false))
        {
            var unavailable = ExternalAccessPolicy.UnavailableReason(saved);

            // Nothing about where the server is, who it authenticates as, or what it is called there —
            // §1.11. The environment label is the exception, and deliberately: it is the user's own word
            // for how dangerous this connection is, and a caller that knows it is pointed at production
            // behaves better than one that does not.
            connections.Add(new ExposedConnection(
                saved.Name,
                EngineName(saved),
                // The access level as a sentence rather than the enum's spelling: it is read by a caller,
                // and "read-only" is the fact, while "ReadOnly" is our identifier for it.
                Access: "read-only",
                Available: unavailable is null)
            {
                Environment = string.IsNullOrWhiteSpace(saved.Environment) ? null : saved.Environment,
                UnavailableReason = unavailable,
            });
        }

        return new ConnectionsResponse(connections);
    }

    public async Task<ICliResponse> ListTablesAsync(string connection, string? schema, CancellationToken ct)
    {
        var info = await ResolveAsync(connection, ct).ConfigureAwait(false);
        using var lease = await ConnectAsync(info, ct).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(info, lease.Session, ct).ConfigureAwait(false);

        return SchemaCatalog.Tables(snapshot, schema);
    }

    public async Task<ICliResponse> DescribeTableAsync(string connection, string table, CancellationToken ct)
    {
        var info = await ResolveAsync(connection, ct).ConfigureAwait(false);
        using var lease = await ConnectAsync(info, ct).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(info, lease.Session, ct).ConfigureAwait(false);

        var (schema, name) = SplitQualified(table);
        var found = snapshot.ResolveTable(schema, name)
            ?? throw new CommandFailure(
                $"'{table}' is not a table or view on '{info.Name}'. "
                + $"Run `bearing {Commands.Tables} {info.Name}` to see what is.");

        return SchemaCatalog.Table(snapshot, found);
    }

    public async Task<ICliResponse> QueryAsync(RunRequest request, CancellationToken ct)
    {
        var (info, traits) = await PrepareAsync(request, ct).ConfigureAwait(false);
        var sql = request.Sql;

        // Settled before anything runs. The check has to happen either way, and finding out that .txt is not
        // an export format is worth nothing after a minute of reading rows nobody will now receive.
        var format = request.OutPath is { } outPath
            ? ExportFormats.For(outPath) ?? throw new CommandFailure($"'{outPath}' is not a .csv or .xlsx path.")
            : (ExportFormat?)null;

        using var lease = await ConnectAsync(info, ct).ConfigureAwait(false);

        // One statement written to a CSV is the shape that streams: rows go to the file a batch at a time
        // and the whole result is never in memory at once, which is what makes `--out` safe to point at a
        // table larger than this process. Everything else materialises — a workbook needs every row to build
        // the sheet (XlsxWriter), and a batch returning several grids is not one table anyway.
        if (format == ExportFormat.Csv && traits.Dialect.SplitStatements(sql).Count == 1)
            return await StreamCsvAsync(info, lease.Session, sql, request.OutPath!, Rows(request), ct)
                .ConfigureAwait(false);

        IReadOnlyList<QueryResult> results;
        var wall = Stopwatch.StartNew();
        try
        {
            results = await lease.Session.Executor
                .ExecuteAsync(sql, new QueryOptions { MaxRows = Rows(request) }, ct)
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

        if (request.OutPath is { } path) return Export(results, path, format!.Value, info.Name);

        // One shape whatever the SQL was. A single statement is the overwhelming case, but a result that
        // is sometimes an object and sometimes a list is a shape a consumer has to branch on, and the
        // branch it is least likely to have written is the rare one.
        return new QueryResponse(results.Select(CellValue.SetOf).ToList());
    }

    /// <summary>
    /// The plan for a statement — and with <see cref="RunRequest.Analyze"/>, the measured one.
    /// <para>
    /// <b>The allow-list judges the caller's statement, and then we send our own.</b> `EXPLAIN ANALYZE`
    /// carries its own <c>BEGIN … ROLLBACK</c> (<c>ExplainSql.Measured</c>), which the allow-list would
    /// refuse if it saw it — rightly, since transaction control from outside is what defeated read-only.
    /// What is validated is what the caller asked to run; what is sent is what we wrapped it in, and the
    /// wrapper is ours rather than theirs.
    /// </para>
    /// <para>
    /// The wrapper is not optional. A plain SELECT can call a volatile function that writes, and ANALYZE
    /// genuinely runs the statement — so the rollback is what keeps "explain this" from being a write. It
    /// is not a promise that nothing happened: a sequence consumed by <c>nextval</c> does not go back, and
    /// neither does anything a trigger did outside the database.
    /// </para>
    /// </summary>
    public async Task<ICliResponse> ExplainAsync(RunRequest request, CancellationToken ct)
    {
        var (info, traits) = await PrepareAsync(request, ct).ConfigureAwait(false);

        // Before anything is sent. ExplainSql's text is Postgres' and ExplainPlanParser reads Postgres' plan
        // JSON back, so on another engine this could only ever hand the caller the server's own syntax error
        // for a command the help said it could run.
        if (!traits.Dialect.SupportsExplainPlan)
            throw new CommandFailure(
                $"'{info.Name}' does not serve query plans through bearing — its engine reports them in a "
                + "form this command cannot read. Run the query itself, or ask for the plan in Bearing.");

        var explain = request.Analyze ? ExplainSql.Measured(request.Sql) : ExplainSql.Plan(request.Sql);

        using var lease = await ConnectAsync(info, ct).ConfigureAwait(false);

        IReadOnlyList<QueryResult> results;
        var wall = Stopwatch.StartNew();
        try
        {
            // No row cap: a plan is one JSON document in one cell, and a ceiling could only truncate it.
            results = await lease.Session.Executor
                .ExecuteAsync(explain.Sql, new QueryOptions { MaxRows = null }, ct)
                .ConfigureAwait(false);
            wall.Stop();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CommandFailure)
        {
            throw new CommandFailure($"The plan could not be read on '{info.Name}': {SafeErrorText.Of(ex)}");
        }

        // Logged as what the caller asked for, not as the wrapped batch: the record is of the statement a
        // person would recognise, and the BEGIN/ROLLBACK around it is ours.
        Log(info, request.Sql, results, wall.Elapsed);

        if (results.FirstOrDefault(r => !r.Success)?.Error is { } error)
            throw new CommandFailure($"{info.Name}: {error.Message}");

        // BEGIN and ROLLBACK produce no rows, so the one result that has any is the plan.
        var json = results.FirstOrDefault(r => r.Rows.Count > 0)?.Rows[0][0]?.ToString();
        var plan = ExplainPlanParser.Parse(json, explain.Analyzed, explain.RolledBack)
            ?? throw new CommandFailure($"{info.Name} did not return a query plan.");

        return PlanTree.Of(plan);
    }

    /// <summary>
    /// How many rows to materialise. The small default is the protection, so it applies only when nobody
    /// said otherwise; an explicit number is honoured whatever its size; and an <b>export is unlimited</b>
    /// unless the caller capped it — a capped export is a silently truncated *file*, which is what the app
    /// guards against with "a workbook missing a sheet is worse than no workbook".
    /// </summary>
    private static int? Rows(RunRequest request)
    {
        if (request.UnlimitedRows) return null;
        if (request.MaxRows is { } asked) return asked;
        return request.OutPath is null ? Commands.DefaultMaxRows : null;
    }

    /// <summary>
    /// Resolve the connection, apply the caller's timeout, and refuse anything that is not a read — what
    /// every run command shares.
    /// </summary>
    private async Task<(ConnectionInfo Info, ProviderTraits Traits)> PrepareAsync(
        RunRequest request, CancellationToken ct)
    {
        var info = WithTimeout(
            await ResolveAsync(request.Connection, ct).ConfigureAwait(false), request.TimeoutSeconds);
        var sql = request.Sql;

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

        return (info, traits);
    }

    /// <summary>
    /// The caller's timeout, applied only if it <b>lowers</b> what the connection already allows.
    /// <para>
    /// Raising it would let a caller lift a limit the connection's owner set, which is the inversion §1.9
    /// exists to prevent: read-only and the statement timeout are the owner's settings, not the caller's.
    /// Lowering is always safe, and "do not let this exploratory query run more than five seconds" is a
    /// thing an agent should be able to say about its own query.
    /// </para>
    /// </summary>
    private static ConnectionInfo WithTimeout(ConnectionInfo exposed, int? seconds)
    {
        if (seconds is not { } asked) return exposed;

        // The exposed record always carries a limit — ExternalAccessPolicy fills one in when the connection
        // has none — so "lower" is simply the smaller of the two.
        var current = SessionPolicy.TimeoutSeconds(exposed);
        return current > SessionPolicy.NoTimeout && asked >= current
            ? exposed
            : exposed with { StatementTimeoutSeconds = asked };
    }

    /// <summary>
    /// Write the grids to a file and report what landed, rather than returning the rows.
    /// <para>
    /// <b>An xlsx takes a sheet per result set; a CSV holds one table.</b> RFC 4180 has no way to express
    /// two tables and the app does not try either — its Export-run is xlsx-only. Refusing beats writing
    /// <c>report.1.csv</c> and <c>report.2.csv</c>: the caller named a path, and a script that then finds
    /// no file at the path it asked for is broken in a way a clear error is not.
    /// </para>
    /// <para>
    /// Only grids count. A statement that returned nothing but a message is not a sheet — an empty tab
    /// named after a <c>SET</c> would be worse than its absence — so <c>set search_path = …; select …</c>
    /// still exports as one table.
    /// </para>
    /// </summary>
    private static ExportResponse Export(
        IReadOnlyList<QueryResult> results, string path, ExportFormat format, string connection)
    {
        var grids = results.Where(r => r.Columns.Count > 0).ToList();
        if (grids.Count == 0)
            throw new CommandFailure($"{connection}: that statement returned no rows to write.");

        if (format == ExportFormat.Csv && grids.Count > 1)
            throw new CommandFailure(
                $"{connection}: the batch returned {grids.Count} result sets, and a CSV holds one table. "
                + "Write a .xlsx instead, or run one statement.");

        try
        {
            if (format == ExportFormat.Xlsx)
            {
                var sheets = grids
                    .Select((g, i) => new XlsxWriter.Sheet(new TableBlock(g.Columns, g.Rows), $"Result {i + 1}"))
                    .ToList();
                ResultExport.WriteWorkbook(path, sheets);
            }
            else
            {
                ResultExport.Write(path, new TableBlock(grids[0].Columns, grids[0].Rows), format, "Result 1");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CommandFailure($"Could not write {path}: {SafeErrorText.Of(ex)}");
        }

        return new ExportResponse(
            Path.GetFullPath(path), ExportFormats.Name(format), grids.Count, grids.Sum(g => (long)g.Rows.Count))
        {
            Truncated = grids.Any(g => g.Truncated),
        };
    }

    /// <summary>
    /// The streamed half of <see cref="Export"/>: one row-returning statement, written to a CSV batch by
    /// batch. The file is the same bytes the materialising path would have produced — both render through
    /// <c>TableFormats.WriteCsvHeader</c>/<c>WriteCsvRow</c> — and the temp-file-and-move means a read that
    /// fails part-way (a timeout, a dropped connection, a server error at row 900,000) leaves no file rather
    /// than a plausible-looking fraction of one.
    /// <para>
    /// The log entry is written here rather than through <see cref="Log"/>, because there is no
    /// <c>QueryResult</c> to take a row count and an error from — the count is what reached the file.
    /// </para>
    /// </summary>
    private async Task<ICliResponse> StreamCsvAsync(
        ConnectionInfo info, ConnectionSession session, string sql, string path, int? maxRows,
        CancellationToken ct)
    {
        var wall = Stopwatch.StartNew();
        ResultExport.StreamedCsv written;
        try
        {
            written = await ResultExport.WriteCsvStreamAsync(
                path,
                session.Executor.StreamRowsAsync(sql, new QueryOptions { MaxRows = maxRows }, ct),
                ct).ConfigureAwait(false);
            wall.Stop();
        }
        // The file, not the statement. Reported as what it is and **not logged as a failed execution**: the
        // statement ran, and an audit row saying it failed with "Could not find a part of the path" is a
        // false record of what happened on the server (§1.11d).
        catch (ResultExport.ExportWriteException ex)
        {
            throw new CommandFailure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CommandFailure)
        {
            // A streamed read reports failure by throwing, so unlike the materialising path there is no
            // unsuccessful QueryResult to log. Recording it by hand keeps a failed export in the history
            // beside a failed query, which is the whole point of logging this path at all (§1.11d).
            wall.Stop();
            var reason = SafeErrorText.Of(ex);
            Write(info, sql, wall.Elapsed, rows: 0, success: false, error: reason);
            throw new CommandFailure($"The query could not be run on '{info.Name}': {reason}");
        }

        // No shape at all — not merely no rows. Nothing was written (WriteCsvStreamAsync moves nothing into
        // place without a header), so this refuses exactly as the materialising path does and leaves any
        // file already at that path alone.
        //
        // Judged *before* the row is logged: the command exits 1 having written no file, and recording that
        // as a successful execution would put a line in the user's history that the command's own exit code
        // contradicts.
        if (written.Columns == 0)
        {
            var refusal = $"{info.Name}: that statement returned no rows to write.";
            Write(info, sql, wall.Elapsed, rows: 0, success: false, error: refusal);
            throw new CommandFailure(refusal);
        }

        Write(info, sql, wall.Elapsed, written.Rows, success: true, error: null);

        return new ExportResponse(
            Path.GetFullPath(path), ExportFormats.Name(ExportFormat.Csv), Results: 1, written.Rows)
        {
            Truncated = written.Truncated,
        };
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
