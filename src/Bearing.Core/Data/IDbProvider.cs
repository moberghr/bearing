using Bearing.Core.Schema;

namespace Bearing.Core.Data;

/// <summary>
/// One per database engine. The registry key for pluggable engines (Postgres in v1; SQL Server /
/// MySQL / DuckDB / SQLite later). Everything DB-touching hangs off this so the rest of the app
/// stays engine-agnostic.
/// </summary>
public interface IDbProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Fields the connect dialog renders for this engine.</summary>
    IReadOnlyList<ConnectionField> ConnectionFields { get; }

    /// <summary>Whether this engine can authenticate as the OS identity
    /// (<see cref="CredentialKind.Integrated"/>). The dialog offers that credential kind only where it
    /// works, rather than knowing per-engine which ones have it.</summary>
    bool SupportsIntegratedAuth { get; }

    /// <summary>
    /// Whether this engine's connection factory can actually authenticate with a short-lived Entra access
    /// token (<see cref="CredentialKind.EntraToken"/>) — that is, whether the token has somewhere to go.
    /// <para>
    /// A separate flag from <see cref="SupportsIntegratedAuth"/> for the same reason that one exists: the
    /// dialog must not offer a credential kind the factory cannot honour. Postgres takes the token as the
    /// password, so it simply works. SQL Server does not — SqlClient accepts a token only through
    /// <c>SqlConnection.AccessToken</c> or an <c>Authentication=Active Directory…</c> mode, never as a
    /// password keyword — so until that path is wired the entry must not appear, rather than appearing and
    /// failing at login.
    /// </para>
    /// </summary>
    bool SupportsEntraToken { get; }

    /// <summary>
    /// Place a failed statement's error (see <see cref="QueryError.SqlState"/>) on the neutral
    /// <see cref="DbErrorKind"/> scale. The engine's codes never leave the provider: the App layer used
    /// to sniff Postgres SQLSTATEs as strings, which quietly mislabelled every other engine's errors.
    /// </summary>
    DbErrorKind Classify(QueryError error);

    /// <summary>
    /// The same judgement for a thrown exception rather than a returned error — what the connect path
    /// has, since a failed handshake never produces a <see cref="QueryError"/>. Implementations should
    /// walk the inner-exception chain: drivers wrap the typed error more often than not.
    /// </summary>
    DbErrorKind ClassifyException(Exception exception);

    IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password);
    IMetadataReader CreateMetadataReader(IDbConnectionFactory factory);
    IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory);
}

/// <summary>Opens/pools underlying connections; hides the concrete ADO.NET driver.</summary>
public interface IDbConnectionFactory : IAsyncDisposable
{
    Task<bool> TestConnectionAsync(CancellationToken ct);
}

public interface IMetadataReader
{
    Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct);

    /// <summary>Bulk catalog read → an immutable snapshot the completion engine can query cheaply.</summary>
    Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct);

    /// <summary>Stored routines (functions/procedures/…) in the reader's database, for schema browsing.</summary>
    Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct);

    /// <summary>Rendered SQL of a view / materialized view, by its table id (<see cref="TableInfo.Id"/>).</summary>
    Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct);

    /// <summary>
    /// Constraints, indexes and triggers of one relation, read on demand (§4.6 of the tree: a table is
    /// expanded far less often than a keystroke happens). Deliberately not part of
    /// <see cref="ISchemaSnapshot"/> — see <see cref="TableDetails"/>.
    /// </summary>
    Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct);

    /// <summary>
    /// Every relation's size in the connected database, keyed by table id (#76).
    /// <para>
    /// One read for the whole database rather than one per table: the sizes come from a single
    /// <c>pg_class</c> join, so fetching them a table at a time would be strictly worse — and the tree wants
    /// them all at once anyway, to sort by them.
    /// </para>
    /// <para>
    /// Not part of <see cref="ISchemaSnapshot"/>, for the same reason as <see cref="TableDetails"/> and one
    /// more: sizes are volatile, so caching them next to structure that is not would make the snapshot stale
    /// in a way nothing else there is. <c>pg_total_relation_size</c> also stats files per relation, which is
    /// not free on a large database — this must never be on the path that renders the tree.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct);

    /// <summary>
    /// Every database's size on the server. A size that cannot be read comes back null rather than throwing:
    /// <c>pg_database_size</c> raises for a database the user cannot connect to, and one inaccessible database
    /// must not cost the sizes of the rest.
    /// </summary>
    Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct);

    /// <summary>Rendered <c>CREATE … FUNCTION/PROCEDURE</c> source, by routine id (<see cref="RoutineInfo.Id"/>).</summary>
    Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct);
}

public interface IQueryExecutor
{
    /// <summary>
    /// Runs the SQL — which may contain several statements separated by <c>;</c> — and returns one
    /// <see cref="QueryResult"/> per result set (SELECTs and RETURNING clauses produce grids;
    /// INSERT/UPDATE/DDL produce a rows-affected message). Always at least one element; a failure
    /// is returned as a single error result rather than thrown.
    /// </summary>
    Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct);

    /// <summary>
    /// Run one already-built page query (the caller shaped the LIMIT/OFFSET — see <c>PageSql.Page</c>)
    /// as a single row-returning statement. One result set, uncapped; base-table origin isn't read
    /// (the columns come from the first page). The executor only runs the SQL — it doesn't shape it.
    /// </summary>
    Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct);

    /// <summary>
    /// Run one already-built row-returning query (the caller shaped any LIMIT/OFFSET — see <c>PageSql</c>)
    /// and yield its rows in <see cref="QueryOptions.BatchRows"/>-sized batches as the reader drains them,
    /// so a large result materializes incrementally instead of in one jump.
    /// <para>
    /// <b>One statement, one snapshot.</b> This is what "fetch all" runs instead of walking pages: every row
    /// comes from the same execution, so a concurrent insert or delete can't shift rows between pages and
    /// make the fetch duplicate or skip them — and the server produces each row once rather than re-running
    /// the query per page with a growing OFFSET.
    /// </para>
    /// <see cref="QueryOptions.MaxRows"/> caps what is yielded and sets <see cref="RowBatch.Truncated"/> on
    /// the final batch when the server still had rows. Base-table origin isn't read (the columns come from
    /// the first page). Failures are <em>thrown</em>, not returned as an error result: a half-consumed stream
    /// has no single result to carry one, and a caller that must not act on half an answer needs the throw.
    /// </summary>
    IAsyncEnumerable<RowBatch> StreamRowsAsync(string sql, QueryOptions options, CancellationToken ct);

    /// <summary>
    /// Run one already-built count query (the caller shaped the <c>select count(*) from (…)</c> wrapper —
    /// see <c>ISqlDialect.CountWrap</c>) and return its scalar. As with <see cref="ExecutePageAsync"/>, the
    /// executor only runs the SQL — it doesn't shape it: the wrapper is dialect-varying text (SQL Server's
    /// needs an <c>OFFSET 0 ROWS</c> repair before a derived table may carry an <c>ORDER BY</c>), and
    /// generating it here would put a second copy of that text next to every driver.
    /// <para>
    /// Null means the query's <em>shape</em> can't be counted (a multi-statement batch, a non-SELECT, a
    /// data-modifying CTE, or — on SQL Server — a derived table with an unnamed or duplicated column) and
    /// the caller should simply show no total. A real failure — connection lost, table dropped, permission
    /// denied, timeout, cancellation — is <em>thrown</em>, never reported as a missing total, so the UI can
    /// say the count failed instead of silently leaving the row count blank.
    /// </para>
    /// </summary>
    Task<long?> CountAsync(string countSql, CancellationToken ct);

    /// <summary>
    /// Run one or more generated writes (UPDATE/DELETE/INSERT) in a single transaction. Returns one
    /// <see cref="QueryResult"/> per command in order (INSERT … RETURNING yields rows; UPDATE/DELETE
    /// yield an affected-rows message). Any failure rolls back the whole batch and is returned as a
    /// single error result rather than thrown.
    /// </summary>
    Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct);
}

/// <summary>Registry of available providers, resolved by <see cref="IDbProvider.Id"/>.</summary>
public interface IProviderRegistry
{
    IDbProvider Get(string providerId);
    IReadOnlyCollection<IDbProvider> All { get; }
}
