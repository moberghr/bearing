using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;

namespace Squirrel.App.Connections;

/// <summary>
/// Default <see cref="IConnectionSessionManager"/>. Live sessions and in-flight connects/schema-loads
/// are guarded by a single lock; each id is connected at most once concurrently (single-flight), and
/// a session whose settings changed is disposed and rebuilt on next use. The secret store is read
/// lazily (it is attached after construction), so a password is fetched fresh at connect time.
/// </summary>
public sealed class ConnectionSessionManager : IConnectionSessionManager
{
    private readonly IProviderRegistry _providers;
    private readonly Func<ISecretStore?> _secretStore;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ConnectionSession> _live = new();
    // Value carries the target Info so a connect in flight for one database isn't reused for another
    // (the toolbar can switch DB on the same connection id while the first connect is still running).
    private readonly Dictionary<Guid, (ConnectionInfo Info, Task<ConnectionSession> Task)> _inflight = new();
    private readonly Dictionary<Guid, Task<ISchemaSnapshot?>> _schemaInflight = new();

    public ConnectionSessionManager(IProviderRegistry providers, Func<ISecretStore?> secretStore)
    {
        _providers = providers;
        _secretStore = secretStore;
    }

    public Task<ConnectionSession> GetOrConnectAsync(ConnectionInfo info, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(info.Id, out var existing) && SameConnection(existing.Info, info))
                return Task.FromResult(existing);
            if (_inflight.TryGetValue(info.Id, out var pending))
            {
                if (SameConnection(pending.Info, info)) return pending.Task;
                // A connect is in flight for a different database on this id — wait for it to settle,
                // then connect the requested database (BuildAsync will dispose the stale session).
                return WaitThenConnectAsync(pending.Task, info, ct);
            }
            var task = BuildAsync(info, ct);
            _inflight[info.Id] = (info, task);
            return task;
        }
    }

    private async Task<ConnectionSession> WaitThenConnectAsync(
        Task<ConnectionSession> prior, ConnectionInfo info, CancellationToken ct)
    {
        try { await prior.ConfigureAwait(false); } catch { /* prior's failure is its caller's concern */ }
        return await GetOrConnectAsync(info, ct).ConfigureAwait(false);
    }

    public ConnectionSession? TryGet(Guid connectionId)
    {
        lock (_gate) return _live.TryGetValue(connectionId, out var s) ? s : null;
    }

    public Task<ISchemaSnapshot?> EnsureSchemaAsync(ConnectionSession session, CancellationToken ct)
    {
        if (session.Snapshot is not null) return Task.FromResult<ISchemaSnapshot?>(session.Snapshot);
        lock (_gate)
        {
            if (session.Snapshot is not null) return Task.FromResult<ISchemaSnapshot?>(session.Snapshot);
            if (_schemaInflight.TryGetValue(session.ConnectionId, out var pending)) return pending;
            var task = LoadSchemaAsync(session, ct);
            _schemaInflight[session.ConnectionId] = task;
            return task;
        }
    }

    public async Task EvictAsync(Guid connectionId)
    {
        ConnectionSession? session;
        lock (_gate)
        {
            _live.TryGetValue(connectionId, out session);
            if (session is not null) _live.Remove(connectionId);
        }
        if (session is not null) await SafeDisposeAsync(session);
    }

    public async ValueTask DisposeAsync()
    {
        ConnectionSession[] all;
        lock (_gate)
        {
            all = _live.Values.ToArray();
            _live.Clear();
            _inflight.Clear();
            _schemaInflight.Clear();
        }
        foreach (var s in all) await SafeDisposeAsync(s);
    }

    private async Task<ConnectionSession> BuildAsync(ConnectionInfo info, CancellationToken ct)
    {
        // Yield so GetOrConnectAsync registers this task in _inflight before the body can finish
        // (a synchronously-completing provider would otherwise run the finally before registration).
        await Task.Yield();
        try
        {
            // A live session may exist but be stale (settings edited) — dispose it before rebuilding.
            ConnectionSession? stale;
            lock (_gate)
            {
                _live.TryGetValue(info.Id, out stale);
                if (stale is not null && SameConnection(stale.Info, info)) return stale;
                if (stale is not null) _live.Remove(info.Id);
            }
            if (stale is not null) await SafeDisposeAsync(stale);

            var store = _secretStore();
            var password = store is null ? null : await store.GetPasswordAsync(info.Id, ct);

            var provider = _providers.Get(info.ProviderId);
            var factory = provider.CreateConnectionFactory(info, password);

            bool ok;
            try { ok = await factory.TestConnectionAsync(ct); }
            catch (Exception ex)
            {
                await SafeDisposeFactoryAsync(factory);
                throw new ConnectionFailedException(Describe(info, ex.Message), ex);
            }
            if (!ok)
            {
                await SafeDisposeFactoryAsync(factory);
                throw new ConnectionFailedException(Describe(info, "connection test failed"));
            }

            var session = new ConnectionSession(
                info, factory, provider.CreateQueryExecutor(factory), provider.CreateMetadataReader(factory));
            lock (_gate) _live[info.Id] = session;
            return session;
        }
        finally
        {
            lock (_gate) _inflight.Remove(info.Id);
        }
    }

    private async Task<ISchemaSnapshot?> LoadSchemaAsync(ConnectionSession session, CancellationToken ct)
    {
        try
        {
            var snapshot = await session.Metadata.LoadSnapshotAsync(session.Info.Database, ct);
            session.Snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            lock (_gate) _schemaInflight.Remove(session.ConnectionId);
        }
    }

    private static string Describe(ConnectionInfo info, string detail)
        => $"Could not connect to '{info.Name}' ({info.Host}:{info.Port}/{info.Database}): {detail}";

    /// <summary>Compares only the fields that define the live connection; name/environment/color are cosmetic.</summary>
    private static bool SameConnection(ConnectionInfo a, ConnectionInfo b)
        => a.ProviderId == b.ProviderId && a.Host == b.Host && a.Port == b.Port
           && a.Database == b.Database && a.User == b.User && SameOptions(a.Options, b.Options);

    private static bool SameOptions(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var bv) || bv != v) return false;
        return true;
    }

    private static async Task SafeDisposeAsync(ConnectionSession session)
    {
        try { await session.DisposeAsync(); } catch { /* best-effort */ }
    }

    private static async Task SafeDisposeFactoryAsync(IDbConnectionFactory factory)
    {
        try { await factory.DisposeAsync(); } catch { /* best-effort */ }
    }
}
