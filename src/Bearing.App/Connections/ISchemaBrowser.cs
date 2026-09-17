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

    /// <summary>
    /// Sequences, user-defined types, extensions and RLS policies for one database (#119).
    /// <para>
    /// Its own call rather than a field on <see cref="DatabaseObjects"/>, following
    /// <see cref="GetRelationSizesAsync"/>: <c>GetObjectsAsync</c> is awaited <em>before</em> the tree
    /// renders, so four more catalog reads there would make every expand slower by all of them. These arrive
    /// after the relations are on screen and add their groups then.
    /// </para>
    /// </summary>
    Task<DatabaseObjectKinds> GetDatabaseObjectKindsAsync(
        ConnectionInfo connection, string database, CancellationToken ct);

    /// <summary>
    /// The server's roles (#120). No database parameter: roles are cluster-wide, so this is asked of the
    /// server and answered through whichever database is reachable — the same shape
    /// <see cref="GetDatabasesAsync"/> has, and for the same reason.
    /// </summary>
    Task<IReadOnlyList<RoleInfo>> GetRolesAsync(ConnectionInfo connection, CancellationToken ct);

    /// <summary>
    /// What one role may do on one database (#120). The roles are the server's; a grant is on an object, and
    /// objects live in a database — so the scope of the answer is narrower than the scope of the question,
    /// and the parameter list says so.
    /// </summary>
    Task<RoleGrants> GetRoleGrantsAsync(
        ConnectionInfo connection, string database, string roleName, CancellationToken ct);

    /// <summary>The server's tablespaces — cluster-wide, like <see cref="GetRolesAsync"/>.</summary>
    Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(ConnectionInfo connection, CancellationToken ct);

    /// <summary>Drop all cached per-database readers for a connection so the next read re-fetches fresh metadata.</summary>
    Task InvalidateAsync(Guid connectionId);
}
