using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.Demo;

/// <summary>
/// A provider that serves <see cref="DemoCatalog"/> — a whole database's worth of behaviour with no database
/// (#63). <c>FakeProvider</c> is a lifecycle stub whose executor returns an empty result from every method;
/// this one returns rows.
/// <para>
/// It implements <see cref="IProviderRegistry"/> as well, which is the swap point, and the composition root
/// is manual <c>new</c> (§2.4) — so putting it in front of the real UI is one argument.
/// </para>
/// </summary>
public sealed class DemoProvider : IDbProvider, IProviderRegistry
{
    private readonly DemoExecutor _executor;

    /// <param name="executor">The executor every session shares, so a test can script it once and then
    /// assert what the UI asked for. Defaults to one serving <see cref="DemoCatalog"/>'s tables by name.</param>
    public DemoProvider(DemoExecutor? executor = null) => _executor = executor ?? DemoExecutor.Default();

    /// <summary>The shared executor — where the results are scripted and the writes are recorded.</summary>
    public DemoExecutor Executor => _executor;

    /// <summary>
    /// The provider id a demo connection carries. Its own, not "postgres": a demo session replaces the
    /// registry wholesale (see <c>DemoMode</c>), so nothing needs it to impersonate the real provider — and a
    /// demo connection that somehow reached a normal session would then fail to resolve rather than quietly
    /// serving fixed data where real data was expected.
    /// </summary>
    public const string ProviderId = "demo";

    public string Id => ProviderId;

    public string DisplayName => "Demo";
    public IReadOnlyList<ConnectionField> ConnectionFields => Array.Empty<ConnectionField>();

    /// <summary>False, and not because the demo could not pretend: a demo session opens no socket, so a
    /// credential kind is a promise about an authentication that never happens. The connect dialog offers
    /// neither, which is honest — and a demo session replaces the registry wholesale anyway, so the dialog
    /// is not normally reachable with this provider selected.</summary>
    public bool SupportsIntegratedAuth => false;

    /// <inheritdoc cref="SupportsIntegratedAuth"/>
    public bool SupportsEntraToken => false;

    /// <summary>
    /// Always <see cref="DbErrorKind.Unknown"/>. The demo executor fails only where a fixture told it to,
    /// and those failures carry no engine code to place on the scale — there is no engine. Returning
    /// Unknown is the honest answer: it makes the App layer fall back to showing the message, which is all
    /// a scripted failure has.
    /// </summary>
    public DbErrorKind Classify(QueryError error) => DbErrorKind.Unknown;

    /// <inheritdoc cref="Classify"/>
    public DbErrorKind ClassifyException(Exception exception) => DbErrorKind.Unknown;

    /// <summary>
    /// Resolves this provider, and only this provider. A demo session's registry holds nothing else, so a
    /// request for "postgres" is a bug worth hearing about rather than something to silently serve fake rows
    /// for — a project file carried into a demo session would otherwise look like it had connected.
    /// </summary>
    public IDbProvider Get(string providerId)
        => string.Equals(providerId, ProviderId, StringComparison.OrdinalIgnoreCase)
            ? this
            : throw new KeyNotFoundException(
                $"'{providerId}' is not available in a demo session — only '{ProviderId}' is.");
    public IReadOnlyCollection<IDbProvider> All => [this];

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
        => new DemoConnectionFactory();

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory) => new DemoMetadata();

    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory) => _executor;
}

/// <summary>A factory whose connections always open. There is nothing to fail against.</summary>
public sealed class DemoConnectionFactory : IDbConnectionFactory
{
    public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DemoMetadata : IMetadataReader
{
    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([DemoCatalog.Database, "postgres"]);

    /// <summary>The demo catalog, whatever database is asked for — named after the request so the schema
    /// browser's per-database caching is still exercised.</summary>
    public Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        var demo = DemoCatalog.Snapshot();
        return Task.FromResult<ISchemaSnapshot>(database == DemoCatalog.Database
            ? demo
            : new SchemaSnapshot(database, demo.Schemas, demo.Tables,
                demo.Tables.SelectMany(t => demo.ColumnsOf(t.Id)).ToList(),
                demo.Tables.SelectMany(t => demo.ForeignKeysTouching(t.Id)).Distinct().ToList(),
                demo.SearchPath));
    }

    public Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
        => Task.FromResult(DemoCatalog.Routines());

    public Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct)
        => Task.FromResult("select p.id as payment_id, s.name as store_name\n"
                           + "from shop.payment p join shop.store s on s.id = p.store_id");

    public Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct)
        => Task.FromResult(DemoCatalog.DetailsOf(tableId));

    public Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct)
        => Task.FromResult(DemoCatalog.Sizes());

    public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
        => Task.FromResult(DemoCatalog.DatabaseSizes());

    public Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct)
        => Task.FromResult("create function shop.gross_revenue(from_date date) returns numeric\n"
                           + "language sql as $$ select sum(amount) from shop.payment $$;");
}

/// <summary>
/// Serves scripted results, matched on what the SQL mentions, and records what it was asked to do.
/// <para>
/// Matching on a substring rather than parsing, deliberately: the point is to put a known result shape in
/// front of the UI, not to be a SQL engine. A test that cares which statement ran asserts on
/// <see cref="Executed"/> or <see cref="Writes"/>.
/// </para>
/// </summary>
public sealed class DemoExecutor : IQueryExecutor
{
    private readonly List<(string Mentions, IReadOnlyList<QueryResult> Results)> _scripted = [];
    private readonly Func<string, IReadOnlyList<string>>? _split;
    private readonly object _gate = new();
    private readonly List<string> _executed = [];
    private readonly List<IReadOnlyList<SqlWriteCommand>> _writes = [];

    /// <param name="splitStatements">
    /// Splits a batch into statements, so a multi-statement run returns one result set per statement as the
    /// real provider does. Injected because splitting SQL correctly needs a lexer and that lives in
    /// <c>Bearing.Sql</c>, which this project may not reference (§2.2) — and a naive split on ';' would cut a
    /// string literal in half. Null runs the whole batch as one statement.
    /// </param>
    public DemoExecutor(Func<string, IReadOnlyList<string>>? splitStatements = null)
        => _split = splitStatements;

    /// <summary>Every SQL string this executor was asked to run, in order. A snapshot: the list is appended
    /// on whichever thread ran the query, and one executor is shared by every tab.</summary>
    public IReadOnlyList<string> Executed
    {
        get { lock (_gate) return _executed.ToList(); }
    }

    /// <summary>Every write batch it was handed, in order — the generated DML, parameters included.</summary>
    public IReadOnlyList<IReadOnlyList<SqlWriteCommand>> Writes
    {
        get { lock (_gate) return _writes.ToList(); }
    }

    /// <summary>
    /// How many statements to remember. One executor serves a whole demo session, so an unbounded record of
    /// every string ever run is a leak on a long one — and nothing needs more than the recent past.
    /// </summary>
    private const int RememberedStatements = 200;

    private void Record(string sql)
    {
        lock (_gate)
        {
            _executed.Add(sql);
            if (_executed.Count > RememberedStatements) _executed.RemoveAt(0);
        }
    }

    /// <summary>What an unmatched query returns. Defaults to an empty grid rather than an error, so an
    /// unscripted query in a UI test is a blank result and not a red banner.</summary>
    public IReadOnlyList<QueryResult> Fallback { get; set; } = [DemoCatalog.NoRows()];

    /// <summary>When set, <see cref="CountAsync"/> throws this instead of counting — a real count failure,
    /// which the UI has to report rather than showing a missing total.</summary>
    public Exception? CountError { get; set; }

    /// <summary>When true, <see cref="CountAsync"/> returns null: the query's <em>shape</em> cannot be
    /// counted, which is a blank total rather than an error.</summary>
    public bool Uncountable { get; set; }

    /// <summary>
    /// An executor that answers each of <see cref="DemoCatalog"/>'s relations.
    /// <para>
    /// Matched on the <b>qualified</b> name, not the bare one: <c>select payment_id … from shop.receipt</c>
    /// contains "payment", so bare patterns served the view's query a payments grid — first registration
    /// wins, and the collision was invisible because both are plausible results.
    /// </para>
    /// </summary>
    public static DemoExecutor Default(Func<string, IReadOnlyList<string>>? splitStatements = null)
    {
        var executor = new DemoExecutor(splitStatements);
        executor.Serve($"{DemoCatalog.Schema}.payment", DemoCatalog.Payments(40));
        executor.Serve($"{DemoCatalog.Schema}.store", DemoCatalog.Stores());
        executor.Serve($"{DemoCatalog.Schema}.document", DemoCatalog.Documents());
        executor.Serve($"{DemoCatalog.Schema}.metric", DemoCatalog.Metrics());
        executor.Serve($"{DemoCatalog.Schema}.receipt", DemoCatalog.ReceiptView());
        return executor;
    }

    /// <summary>Answer any SQL mentioning <paramref name="mentions"/> with these results. First match wins,
    /// so register the more specific pattern first.</summary>
    public DemoExecutor Serve(string mentions, params QueryResult[] results)
    {
        _scripted.Add((mentions, results));
        return this;
    }

    /// <summary>
    /// Run the batch, returning one result set per statement in it — which is what the real executor does,
    /// and what the demo's own welcome script tells the user to try.
    /// </summary>
    public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(sql);

        var statements = _split?.Invoke(sql) ?? [sql];
        var results = statements.Count <= 1
            ? Match(sql)
            // Each set says which statement it came from, as the real provider does — that is what lets one
            // grid of a batch be copied with its own query rather than with all of them. Positional, and
            // only where it holds: a fixture whose pattern serves two sets for one statement breaks the
            // one-set-per-statement correspondence, and an index the caller can't trust is worse than none
            // (see QueryResult.StatementIndex).
            : Attributed(statements.Select(s => Match(s)).ToList());
        return Task.FromResult(Cap(results, options.MaxRows));
    }

    /// <summary>Flatten one batch's per-statement results, numbering them when each statement produced
    /// exactly one set — the condition <see cref="QueryResult.StatementIndex"/> is defined on.</summary>
    private static List<QueryResult> Attributed(List<IReadOnlyList<QueryResult>> perStatement)
    {
        var oneEach = perStatement.All(r => r.Count == 1);
        return perStatement
            .SelectMany((sets, i) => sets.Select(r => oneEach ? r with { StatementIndex = i } : r))
            .ToList();
    }

    /// <summary>
    /// One page of an already-shaped paging query. The columns come back <b>without</b> origins, as they do
    /// from the real provider: the base table of a wrapped paging query is not read, so a fixture that kept
    /// them would make the UI look more capable on page two than it is.
    /// </summary>
    public Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(pageSql);
        var result = First(pageSql);
        return Task.FromResult(result with
        {
            Columns = result.Columns
                .Select(c => new ColumnDescriptor(c.Name, c.DataTypeName, c.ClrType))
                .ToList(),
        });
    }

    /// <summary>
    /// The matched result's rows in <see cref="QueryOptions.BatchRows"/>-sized batches, capped by
    /// <see cref="QueryOptions.MaxRows"/>. <c>Truncated</c> is set on the final batch only when the cap is
    /// what stopped it and rows were still waiting — the distinction the incremental read depends on.
    /// </summary>
    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        Record(sql);
        var rows = First(sql).Rows;
        var ceiling = options.MaxRows ?? rows.Count;
        var yielded = 0;
        var batch = Math.Max(1, options.BatchRows);

        while (yielded < Math.Min(ceiling, rows.Count))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();   // a real reader hands control back; a test needs the same interleaving
            var take = Math.Min(batch, Math.Min(ceiling, rows.Count) - yielded);
            yielded += take;
            var truncated = yielded == ceiling && rows.Count > ceiling;
            yield return new RowBatch(rows.Skip(yielded - take).Take(take).ToList(), truncated);
        }
    }

    public Task<long?> CountAsync(string sql, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (CountError is not null) throw CountError;
        return Task.FromResult(Uncountable ? null : (long?)First(sql).Rows.Count);
    }

    /// <summary>Runs the batch as one transaction — which here means recording it and reporting one
    /// affected row per command, in order.</summary>
    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
        IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) _writes.Add(commands);
        return Task.FromResult<IReadOnlyList<QueryResult>>(
            commands.Select(c => DemoCatalog.Affected(1) with { Message = Verb(c.Sql) + " 1" }).ToList());
    }

    /// <summary>
    /// The first result for a query, for the paths that can only return one (a page, a count, a stream).
    /// Falls back to an empty grid rather than indexing into nothing: <c>Serve("x")</c> with no results
    /// compiles, and <see cref="Fallback"/> can be set to an empty list.
    /// </summary>
    private QueryResult First(string sql)
        => Match(sql) is { Count: > 0 } results ? results[0] : DemoCatalog.NoRows();

    private IReadOnlyList<QueryResult> Match(string sql)
    {
        foreach (var (mentions, results) in _scripted)
            if (sql.Contains(mentions, StringComparison.OrdinalIgnoreCase))
                return results;
        return Fallback;
    }

    /// <summary>Apply the row ceiling the UI asked for, marking what it cut — the grid's protection against
    /// a runaway result, and a state the fixtures have to be able to produce.</summary>
    private static IReadOnlyList<QueryResult> Cap(IReadOnlyList<QueryResult> results, int? maxRows)
    {
        if (maxRows is not { } max) return results;
        return results
            .Select(r => r.Rows.Count <= max
                ? r
                : r with { Rows = r.Rows.Take(max).ToList(), Truncated = true })
            .ToList();
    }

    private static string Verb(string sql)
    {
        var word = sql.TrimStart().Split(' ', 2)[0].ToUpperInvariant();
        return word is "INSERT" or "UPDATE" or "DELETE" ? word : "UPDATE";
    }
}
