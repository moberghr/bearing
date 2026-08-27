using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.App.Connections;

/// <summary>
/// Caches one live <see cref="ConnectionSession"/> per <see cref="SessionKey"/> — connection <i>and</i>
/// database — so tabs pointed at the same database reuse the same pool, and pointing a tab at another
/// database on the same server opens a second pool instead of tearing the first one down (#54). Sessions are
/// created lazily on first execution and disposed on eviction, project switch, or shutdown.
///
/// <para>Two granularities, and the distinction is the point. A <b>pool</b> is per (connection, database),
/// because Postgres binds a connection to a database at startup and there is no <c>USE</c>. A <b>server
/// link</b> (<see cref="IsLinked"/>) is per connection: it says we resolved credentials and completed a
/// handshake to that server, and it is what every connected/disconnected indicator in the UI reads. Pools
/// come and go underneath a link — opened lazily per database, reclaimed by the idle sweep — without the
/// user's answer to "am I connected to this server?" changing.</para>
/// </summary>
public interface IConnectionSessionManager : IAsyncDisposable
{
    /// <summary>Raised when a session enters or leaves the live-session map — a connect landing, an evict, an
    /// idle-sweep, or a project close. The argument is the affected <see cref="SessionKey"/>; a handler
    /// re-reads <see cref="TryGet"/> to learn the new state. It carries the database because a connection can
    /// now have several live sessions, and "connection X changed" would not say which pool moved. May fire on
    /// a background thread, so marshal to the UI thread before touching bound state.</summary>
    event Action<SessionKey>? LiveChanged;

    /// <summary>Raised when a connection gains or loses its server link — a first successful handshake, an
    /// explicit disconnect, a connection edited/deleted, a credential expiring, or a project close. Coarser
    /// and rarer than <see cref="LiveChanged"/>: a pool opening on a second database of an already-linked
    /// server does not fire it, and neither does the idle sweep reclaiming pools. Handlers re-read
    /// <see cref="IsLinked"/>. May fire on a background thread.</summary>
    event Action<Guid>? LinkChanged;

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

    /// <summary>The already-live session for a connection+database, or null. Never connects.</summary>
    ConnectionSession? TryGet(SessionKey key);

    /// <summary>True when <i>any</i> database on this connection has a live session right now. This is the
    /// pool question, not the connected question — use <see cref="IsLinked"/> for anything the user sees.</summary>
    bool IsAnyLive(Guid connectionId);

    /// <summary>True when we have a server link to this connection: credentials resolved and a handshake
    /// completed, not since torn down. <b>This is what "connected" means to the user</b>, and what the
    /// toolbar dot, the tab chain glyphs and the schema tree's server row all read, so that they agree with
    /// each other and stay stable while a tab moves between databases or the idle sweep reclaims a pool.
    /// It can be true with no pool open — the next query rebuilds one from the cached credential.</summary>
    bool IsLinked(Guid connectionId);

    /// <summary>Load the schema snapshot for a session if not already loaded (idempotent, single-flight).</summary>
    Task<ISchemaSnapshot?> EnsureSchemaAsync(ConnectionSession session, CancellationToken ct);

    /// <summary>The last snapshot read for this connection+database, live session or not. Snapshots outlive
    /// sessions deliberately, so completion still works while disconnected.</summary>
    ISchemaSnapshot? TryGetSnapshot(Guid connectionId, string database);

    /// <summary>Forget the cached schema for a connection — only for events that make the catalog untrue
    /// (re-pointed at another server, deleted, explicitly refreshed). A disconnect is not one of them.</summary>
    void InvalidateSchema(Guid connectionId);

    /// <summary>Drop and dispose the session for one connection+database — the pool that was just cancelled
    /// mid-connect, or the one whose credential a retry is about to refresh. Other databases on the same
    /// connection keep their pools. Deliberately keeps the cached schema — see <see cref="InvalidateSchema"/>.
    /// Drops the server link only if this removed the connection's last live session.</summary>
    Task EvictAsync(SessionKey key);

    /// <summary>Drop and dispose <i>every</i> database's session on a connection — the toolbar Disconnect
    /// ("disconnect from server"), a connection edited/deleted/re-pointed, or a project close. Use this and
    /// not <see cref="EvictAsync(SessionKey)"/> whenever the reason is about the server rather than one
    /// database, or the schema tree's server row would stay linked right after a disconnect.</summary>
    Task EvictConnectionAsync(Guid connectionId);

    /// <summary>Close every live/in-flight session — e.g. on a project switch — while keeping the manager
    /// usable for the next project. Unlike <see cref="IAsyncDisposable.DisposeAsync"/> (which retires the
    /// manager for good and ignores leases), this honors an outstanding <see cref="SessionLease"/>: a
    /// session a background query is still using leaves the live map but is disposed only once that query
    /// releases it.</summary>
    Task CloseAllAsync();
}
