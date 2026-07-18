using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;

namespace Squirrel.App.Connections;

/// <summary>The relations + routines of one database, for the schema browser tree.</summary>
public sealed record DatabaseObjects(ISchemaSnapshot Snapshot, IReadOnlyList<PgRoutine> Routines);

/// <summary>
/// Read-only metadata access for the sidebar schema tree. Unlike <see cref="IConnectionSessionManager"/>
/// (one session per connection id, bound to that connection's database), a browser can reach <b>any</b>
/// database on the server by opening a per-database connection on demand — which is what "expand the
/// server to see all its databases" requires. Kept separate so the editor's query path is untouched.
/// </summary>
public interface ISchemaBrowser : IAsyncDisposable
{
    /// <summary>All databases on the server (queried via any reachable database on it).</summary>
    Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct);

    /// <summary>Relations + routines of one database (opens/reuses a per-database connection).</summary>
    Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct);

    Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, uint relOid, CancellationToken ct);
    Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, uint routineOid, CancellationToken ct);

    /// <summary>Drop all cached per-database readers for a connection so the next read re-fetches fresh metadata.</summary>
    Task InvalidateAsync(Guid connectionId);
}
