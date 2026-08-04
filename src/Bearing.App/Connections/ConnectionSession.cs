using System;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.App.Connections;

/// <summary>
/// One live connection: the factory (owns the pool), an executor and metadata reader built on it,
/// and the lazily-loaded schema snapshot for completion. Shared by every editor tab that targets
/// the same connection id. Disposing it tears down the underlying pool.
/// </summary>
public sealed class ConnectionSession : IAsyncDisposable
{
    public ConnectionSession(ConnectionInfo info, IDbConnectionFactory factory, IQueryExecutor executor, IMetadataReader metadata,
        DateTimeOffset? credentialExpiresAt = null)
    {
        ConnectionId = info.Id;
        Info = info;
        Factory = factory;
        Executor = executor;
        Metadata = metadata;
        CredentialExpiresAt = credentialExpiresAt;
    }

    public Guid ConnectionId { get; }

    /// <summary>The settings snapshot this session was built from; used to detect edits that require a rebuild.</summary>
    public ConnectionInfo Info { get; }

    /// <summary>When the credential this session was built with expires (short-lived Entra tokens), or null
    /// for a fixed password. Drives disconnect-before-expiry in <see cref="ConnectionSessionManager"/>.</summary>
    public DateTimeOffset? CredentialExpiresAt { get; }

    public IDbConnectionFactory Factory { get; }
    public IQueryExecutor Executor { get; }
    public IMetadataReader Metadata { get; }

    /// <summary>Schema for completion; null until the first <see cref="IConnectionSessionManager.EnsureSchemaAsync"/>.</summary>
    public ISchemaSnapshot? Snapshot { get; internal set; }

    // Lifetime bookkeeping — all mutated only under the manager's lock. A session is disposed only
    // when it is no longer live AND has no active leases, so a running query is never pulled out from
    // under. LeaseCount > 0 also exempts it from idle eviction.
    internal int LeaseCount;
    internal DateTime LastUsedUtc;
    /// <summary>Removed from the live map (evicted / rebuilt / swept) but kept alive until its last lease
    /// releases; disposed then. New requests build a fresh session rather than reuse a retired one.</summary>
    internal bool Retired;

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}
