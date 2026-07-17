using System;
using System.Threading.Tasks;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;

namespace Squirrel.App.Connections;

/// <summary>
/// One live connection: the factory (owns the pool), an executor and metadata reader built on it,
/// and the lazily-loaded schema snapshot for completion. Shared by every editor tab that targets
/// the same connection id. Disposing it tears down the underlying pool.
/// </summary>
public sealed class ConnectionSession : IAsyncDisposable
{
    public ConnectionSession(ConnectionInfo info, IDbConnectionFactory factory, IQueryExecutor executor, IMetadataReader metadata)
    {
        ConnectionId = info.Id;
        Info = info;
        Factory = factory;
        Executor = executor;
        Metadata = metadata;
    }

    public Guid ConnectionId { get; }

    /// <summary>The settings snapshot this session was built from; used to detect edits that require a rebuild.</summary>
    public ConnectionInfo Info { get; }

    public IDbConnectionFactory Factory { get; }
    public IQueryExecutor Executor { get; }
    public IMetadataReader Metadata { get; }

    /// <summary>Schema for completion; null until the first <see cref="IConnectionSessionManager.EnsureSchemaAsync"/>.</summary>
    public ISchemaSnapshot? Snapshot { get; internal set; }

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}
