using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;

namespace Bearing.App.Connections;

/// <summary>
/// Default <see cref="ISchemaBrowser"/>. Caches one <see cref="IDbConnectionFactory"/> +
/// <see cref="IMetadataReader"/> per <c>(connection id, database)</c>, built lazily via the provider.
/// The password is fetched from the secret store keyed by the <b>parent</b> connection id (cloning the
/// connection for a different database does not change the credential key). Builds are single-flight;
/// <see cref="DisposeAsync"/> tears down every pooled connection.
/// </summary>
public sealed class SchemaBrowser : ISchemaBrowser
{
    private readonly IProviderRegistry _providers;
    private readonly Func<CredentialResolver?> _credentials;

    private readonly object _gate = new();
    private readonly Dictionary<(Guid, string), Task<Reader>> _readers = new();

    public SchemaBrowser(IProviderRegistry providers, Func<CredentialResolver?> credentials)
    {
        _providers = providers;
        _credentials = credentials;
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
    {
        // pg_database is cluster-wide — the connection's own database can answer it.
        var reader = await GetReaderAsync(connection, connection.Database, ct);
        return await reader.Metadata.GetDatabasesAsync(ct);
    }

    public async Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
    {
        var reader = await GetReaderAsync(connection, database, ct);
        var snapshot = await reader.Metadata.LoadSnapshotAsync(database, ct);
        var routines = await reader.Metadata.GetRoutinesAsync(ct);
        return new DatabaseObjects(snapshot, routines);
    }

    public async Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct)
    {
        var reader = await GetReaderAsync(connection, database, ct);
        return await reader.Metadata.GetViewDefinitionAsync(tableId, ct);
    }

    public async Task<TableDetails> GetTableDetailsAsync(
        ConnectionInfo connection, string database, long tableId, CancellationToken ct)
    {
        var reader = await GetReaderAsync(connection, database, ct);
        return await reader.Metadata.GetTableDetailsAsync(tableId, ct);
    }

    public async Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct)
    {
        var reader = await GetReaderAsync(connection, database, ct);
        return await reader.Metadata.GetRoutineDefinitionAsync(routineId, ct);
    }

    private Task<Reader> GetReaderAsync(ConnectionInfo connection, string database, CancellationToken ct)
    {
        var key = (connection.Id, database);
        lock (_gate)
        {
            if (_readers.TryGetValue(key, out var existing)) return existing;
            var task = BuildAsync(connection, database, ct);
            _readers[key] = task;
            // Don't cache a failed (or cancelled) build — the next expand should retry, e.g. after fixing
            // credentials. Evict only if this attempt is *still* the cached one: the unconditional Remove
            // this replaced could drop a concurrent replacement, leaking that reader's factory forever.
            _ = task.ContinueWith(
                t =>
                {
                    lock (_gate)
                        if (_readers.TryGetValue(key, out var current) && ReferenceEquals(current, t))
                            _readers.Remove(key);
                },
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion,
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task<Reader> BuildAsync(ConnectionInfo connection, string database, CancellationToken ct)
    {
        await Task.Yield();
        // Eviction of a failed build is the caller's continuation (see GetReaderAsync), so that it can check
        // this task is still the cached one before removing it.
        var clone = connection with { Database = database };
        var (provider, factory, _) = await ConnectionFactoryBuilder.BuildAsync(_providers, _credentials(), clone, forceRefresh: false, ct);
        return new Reader(factory, provider.CreateMetadataReader(factory));
    }

    public async Task InvalidateAsync(Guid connectionId)
    {
        var pending = new List<Task<Reader>>();
        lock (_gate)
        {
            foreach (var key in _readers.Keys.Where(k => k.Item1 == connectionId).ToList())
            {
                pending.Add(_readers[key]);
                _readers.Remove(key);
            }
        }
        await DisposeAllAsync(pending);
    }

    public async ValueTask DisposeAsync()
    {
        Task<Reader>[] pending;
        lock (_gate)
        {
            pending = _readers.Values.ToArray();
            _readers.Clear();
        }
        await DisposeAllAsync(pending);
    }

    private static async Task DisposeAllAsync(IEnumerable<Task<Reader>> readers)
    {
        foreach (var task in readers)
        {
            try { await (await task).Factory.DisposeAsync(); } catch { /* best-effort */ }
        }
    }

    private sealed record Reader(IDbConnectionFactory Factory, IMetadataReader Metadata);
}
