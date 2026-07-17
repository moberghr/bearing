using System;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;

namespace Squirrel.App.Connections;

/// <summary>
/// Caches one live <see cref="ConnectionSession"/> per connection id so tabs sharing a connection
/// reuse the same pool. Sessions are created lazily on first execution and disposed on eviction,
/// project switch, or shutdown.
/// </summary>
public interface IConnectionSessionManager : IAsyncDisposable
{
    /// <summary>
    /// Return the live session for <paramref name="info"/>, creating and testing it on first use.
    /// Reuses an existing session only if its settings still match; a changed connection is rebuilt.
    /// Throws <see cref="ConnectionFailedException"/> if the connection cannot be established.
    /// </summary>
    Task<ConnectionSession> GetOrConnectAsync(ConnectionInfo info, CancellationToken ct);

    /// <summary>The already-live session for an id, or null. Never connects.</summary>
    ConnectionSession? TryGet(Guid connectionId);

    /// <summary>Load the schema snapshot for a session if not already loaded (idempotent, single-flight).</summary>
    Task<ISchemaSnapshot?> EnsureSchemaAsync(ConnectionSession session, CancellationToken ct);

    /// <summary>Drop and dispose the session for an id (e.g. after the connection is edited or deleted).</summary>
    Task EvictAsync(Guid connectionId);
}
