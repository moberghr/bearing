using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.App.Tests.Demo;

/// <summary>
/// A provider that serves <see cref="DemoData"/> — a whole database's worth of behaviour with no database
/// (#63). <c>FakeProvider</c> is a lifecycle stub whose executor returns an empty result from every method;
/// this one returns rows.
/// <para>
/// It implements <see cref="IProviderRegistry"/> as well, which is the swap point, and the composition root
/// is manual <c>new</c> (§2.4) — so putting it in front of the real UI is one argument.
/// </para>
/// </summary>
internal sealed class DemoProvider : IDbProvider, IProviderRegistry
{
    private readonly DemoExecutor _executor;

    /// <param name="executor">The executor every session shares, so a test can script it once and then
    /// assert what the UI asked for. Defaults to one serving <see cref="DemoData"/>'s tables by name.</param>
    public DemoProvider(DemoExecutor? executor = null) => _executor = executor ?? DemoExecutor.Default();

    /// <summary>The shared executor — where the results are scripted and the writes are recorded.</summary>
    public DemoExecutor Executor => _executor;

    /// <summary>Matches the real Postgres provider's id, so a stored <see cref="ConnectionInfo"/> resolves
    /// to this without being rewritten.</summary>
    public string Id => "postgres";

    public string DisplayName => "Demo";
    public IReadOnlyList<ConnectionField> ConnectionFields => Array.Empty<ConnectionField>();

    public IDbProvider Get(string providerId) => this;
    public IReadOnlyCollection<IDbProvider> All => [this];

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
        => new DemoConnectionFactory();

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory) => new DemoMetadata();

    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory) => _executor;
}

/// <summary>A factory whose connections always open. There is nothing to fail against.</summary>
internal sealed class DemoConnectionFactory : IDbConnectionFactory
{
    public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class DemoMetadata : IMetadataReader
{
    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([DemoData.Database, "postgres"]);

    /// <summary>The demo catalog, whatever database is asked for — named after the request so the schema
    /// browser's per-database caching is still exercised.</summary>
    public Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        var demo = DemoData.Snapshot();
        return Task.FromResult<ISchemaSnapshot>(database == DemoData.Database
            ? demo
            : new SchemaSnapshot(database, demo.Schemas, demo.Tables,
                demo.Tables.SelectMany(t => demo.ColumnsOf(t.Id)).ToList(),
                demo.Tables.SelectMany(t => demo.ForeignKeysTouching(t.Id)).Distinct().ToList(),
                demo.SearchPath));
    }

    public Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
        => Task.FromResult(DemoData.Routines());

    public Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct)
        => Task.FromResult("select p.id as payment_id, s.name as store_name\n"
                           + "from shop.payment p join shop.store s on s.id = p.store_id");

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
internal sealed class DemoExecutor : IQueryExecutor
{
    private readonly List<(string Mentions, IReadOnlyList<QueryResult> Results)> _scripted = [];

    /// <summary>Every SQL string this executor was asked to run, in order.</summary>
    public List<string> Executed { get; } = [];

    /// <summary>Every write batch it was handed, in order — the generated DML, parameters included.</summary>
    public List<IReadOnlyList<SqlWriteCommand>> Writes { get; } = [];

    /// <summary>What an unmatched query returns. Defaults to an empty grid rather than an error, so an
    /// unscripted query in a UI test is a blank result and not a red banner.</summary>
    public IReadOnlyList<QueryResult> Fallback { get; set; } = [DemoData.NoRows()];

    /// <summary>When set, <see cref="CountAsync"/> throws this instead of counting — a real count failure,
    /// which the UI has to report rather than showing a missing total.</summary>
    public Exception? CountError { get; set; }

    /// <summary>When true, <see cref="CountAsync"/> returns null: the query's <em>shape</em> cannot be
    /// counted, which is a blank total rather than an error.</summary>
    public bool Uncountable { get; set; }

    /// <summary>An executor that answers each of <see cref="DemoData"/>'s tables by name.</summary>
    public static DemoExecutor Default()
    {
        var executor = new DemoExecutor();
        executor.Serve("payment", DemoData.Payments(40));
        executor.Serve("store", DemoData.Stores());
        executor.Serve("document", DemoData.Documents());
        executor.Serve("metric", DemoData.Metrics());
        executor.Serve("receipt", DemoData.ReceiptView());
        return executor;
    }

    /// <summary>Answer any SQL mentioning <paramref name="mentions"/> with these results. First match wins,
    /// so register the more specific pattern first.</summary>
    public DemoExecutor Serve(string mentions, params QueryResult[] results)
    {
        _scripted.Add((mentions, results));
        return this;
    }

    public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Executed.Add(sql);
        return Task.FromResult(Cap(Match(sql), options.MaxRows));
    }

    /// <summary>
    /// One page of an already-shaped paging query. The columns come back <b>without</b> origins, as they do
    /// from the real provider: the base table of a wrapped paging query is not read, so a fixture that kept
    /// them would make the UI look more capable on page two than it is.
    /// </summary>
    public Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Executed.Add(pageSql);
        var result = Match(pageSql)[0];
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
        Executed.Add(sql);
        var rows = Match(sql)[0].Rows;
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
        return Task.FromResult(Uncountable ? null : (long?)Match(sql)[0].Rows.Count);
    }

    /// <summary>Runs the batch as one transaction — which here means recording it and reporting one
    /// affected row per command, in order.</summary>
    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
        IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Writes.Add(commands);
        return Task.FromResult<IReadOnlyList<QueryResult>>(
            commands.Select(c => DemoData.Affected(1) with { Message = Verb(c.Sql) + " 1" }).ToList());
    }

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
