using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.App.Connections;

/// <summary>The relations + routines of one database, for the schema browser tree.</summary>
public sealed record DatabaseObjects(ISchemaSnapshot Snapshot, IReadOnlyList<RoutineInfo> Routines);

/// <summary>
/// Read-only metadata access for the sidebar schema tree. Unlike <see cref="IConnectionSessionManager"/>
/// (whose sessions are opened lazily, one per database a tab actually targets), a browser can reach
/// <b>any</b> database on the server by opening a per-database connection on demand — which is what "expand
/// the server to see all its databases" requires. Kept separate so the editor's query path is untouched.
/// </summary>
public interface ISchemaBrowser : IAsyncDisposable
{
    /// <summary>All databases on the server (queried via any reachable database on it).</summary>
    Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct);

    /// <summary>Relations + routines of one database (opens/reuses a per-database connection).</summary>
    Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct);

    Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct);

    /// <summary>
    /// Constraints, indexes and triggers of one relation, read when the user expands it (#46). Per-table and
    /// on demand, following <see cref="GetViewDefinitionAsync"/>, because this is metadata the completion
    /// snapshot deliberately does not carry — see <see cref="TableDetails"/>.
    /// </summary>
    Task<TableDetails> GetTableDetailsAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct);

    Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct);

    /// <summary>
    /// Every relation's size in one database (#76), and every database's size on the server. Separate from
    /// <see cref="GetObjectsAsync"/> because these are the expensive reads —
    /// <c>pg_total_relation_size</c> stats files — and the tree must render before them, not after.
    /// </summary>
    Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(ConnectionInfo connection, string database, CancellationToken ct);

    Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(ConnectionInfo connection, CancellationToken ct);

    /// <summary>Drop all cached per-database readers for a connection so the next read re-fetches fresh metadata.</summary>
    Task InvalidateAsync(Guid connectionId);
}
