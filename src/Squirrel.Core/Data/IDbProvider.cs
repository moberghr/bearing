using Squirrel.Core.Schema;

namespace Squirrel.Core.Data;

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
    /// Fetch one page of a single SELECT by wrapping it — <c>select * from (&lt;sql&gt;) offset..limit..</c>
    /// — for "load more" paging. The SQL must be a single row-returning statement.
    /// </summary>
    Task<QueryResult> ExecutePageAsync(string sql, int offset, int limit, CancellationToken ct);

    /// <summary>Total row count of a single SELECT (<c>select count(*) from (&lt;sql&gt;)</c>); null if it can't be counted.</summary>
    Task<long?> CountAsync(string sql, CancellationToken ct);

    /// <summary>
    /// Run one or more generated writes (UPDATE/DELETE/INSERT) in a single transaction. Returns one
    /// <see cref="QueryResult"/> per command in order (INSERT … RETURNING yields rows; UPDATE/DELETE
    /// yield an affected-rows message). Any failure rolls back the whole batch and is returned as a
    /// single error result rather than thrown.
    /// </summary>
    Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct);

    IAsyncEnumerable<ResultBatch> StreamAsync(string sql, QueryOptions options, CancellationToken ct);
}

/// <summary>Registry of available providers, resolved by <see cref="IDbProvider.Id"/>.</summary>
public interface IProviderRegistry
{
    IDbProvider Get(string providerId);
    IReadOnlyCollection<IDbProvider> All { get; }
}
