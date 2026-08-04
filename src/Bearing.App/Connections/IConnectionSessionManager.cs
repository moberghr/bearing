using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.App.Connections;

/// <summary>
/// Caches one live <see cref="ConnectionSession"/> per connection id so tabs sharing a connection
/// reuse the same pool. Sessions are created lazily on first execution and disposed on eviction,
/// project switch, or shutdown.
/// </summary>
public interface IConnectionSessionManager : IAsyncDisposable
{
    /// <summary>Raised when a connection id enters or leaves the live-session map — a connect landing, an
    /// evict, an idle-sweep, or a project close. The argument is the affected connection id; a handler
    /// re-reads <see cref="TryGet"/> to learn the new state. May fire on a background thread, so marshal
    /// to the UI thread before touching bound state.</summary>
    event Action<Guid>? LiveChanged;

    /// <summary>
    /// Return the live session for <paramref name="info"/>, creating and testing it on first use.
    /// Reuses an existing session only if its settings still match; a changed connection is rebuilt.
    /// Throws <see cref="ConnectionFailedException"/> if the connection cannot be established.
    /// </summary>
    Task<ConnectionSession> GetOrConnectAsync(ConnectionInfo info, CancellationToken ct);

    /// <summary>
    /// Connect (or reuse) as <see cref="GetOrConnectAsync"/> does, and atomically take a lease that keeps
    /// the session from being disposed while the returned lease is held. Dispose the lease when the query
    /// finishes. Use this around any execution so an idle sweep / evict / database switch can't tear the
    /// pool down mid-query.
    /// </summary>
    Task<SessionLease> AcquireAsync(ConnectionInfo info, CancellationToken ct);

    /// <summary>Take a lease on an already-live session (for follow-up work like paging/count on a result).</summary>
    SessionLease Lease(ConnectionSession session);

    /// <summary>The already-live session for an id, or null. Never connects.</summary>
    ConnectionSession? TryGet(Guid connectionId);

    /// <summary>Load the schema snapshot for a session if not already loaded (idempotent, single-flight).</summary>
    Task<ISchemaSnapshot?> EnsureSchemaAsync(ConnectionSession session, CancellationToken ct);

    /// <summary>Drop and dispose the session for an id (e.g. after the connection is edited or deleted).</summary>
    Task EvictAsync(Guid connectionId);

    /// <summary>Close and dispose every live/in-flight session — e.g. on a project switch — while keeping
    /// the manager usable for the next project. Unlike <see cref="IAsyncDisposable.DisposeAsync"/> (which
    /// retires the manager for good), the manager can connect again afterwards.</summary>
    Task CloseAllAsync();
}
