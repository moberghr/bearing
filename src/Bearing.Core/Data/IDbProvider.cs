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

    /// <summary>Total row count of a single SELECT (<c>select count(*) from (&lt;sql&gt;)</c>); null if it can't be counted.</summary>
    Task<long?> CountAsync(string sql, CancellationToken ct);

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
