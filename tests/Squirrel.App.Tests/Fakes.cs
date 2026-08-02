using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.App.Connections;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;

namespace Squirrel.App.Tests;

/// <summary>A provider whose factories/readers are controllable and counted, for session-manager tests.</summary>
internal sealed class FakeProvider : IDbProvider, IProviderRegistry
{
    public int FactoriesCreated;
    public bool TestResult = true;
    public Exception? TestThrows;

    public string Id => "postgres";
    public string DisplayName => "Fake";
    public IReadOnlyList<ConnectionField> ConnectionFields => System.Array.Empty<ConnectionField>();

    public IDbProvider Get(string providerId) => this;
    public IReadOnlyCollection<IDbProvider> All => new[] { (IDbProvider)this };

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
    {
        Interlocked.Increment(ref FactoriesCreated);
        LastPassword = password;
        return new FakeFactory { TestResult = TestResult, TestThrows = TestThrows, Gate = ConnectGate };
    }

    /// <summary>When set, every factory's connection test blocks on this gate — lets a test observe an
    /// attempt mid-flight (Connecting) and then cancel or complete it deterministically.</summary>
    public TaskCompletionSource<bool>? ConnectGate;

    public string? LastPassword;

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory) => new FakeMetadata((FakeFactory)factory);

    /// <summary>When set, every session built by this provider shares this executor (so a test can gate
    /// concurrent runs across tabs). Otherwise each session gets a fresh no-op <see cref="FakeExecutor"/>.</summary>
    public IQueryExecutor? Executor;
    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory) => Executor ?? new FakeExecutor();
}

internal sealed class FakeFactory : IDbConnectionFactory
{
    public bool TestResult = true;
    public Exception? TestThrows;
    public TaskCompletionSource<bool>? Gate;
    public int DisposeCount;

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        if (TestThrows is not null) throw TestThrows;
        if (Gate is not null) await Gate.Task.WaitAsync(ct); // blocks until released, throws on cancel
        return TestResult;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref DisposeCount);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeMetadata : IMetadataReader
{
    private readonly FakeFactory _factory;
    public int LoadCount;
    public FakeMetadata(FakeFactory factory) => _factory = factory;

    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(new[] { "app" });

    public Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        Interlocked.Increment(ref LoadCount);
        return Task.FromResult<ISchemaSnapshot>(new FakeSnapshot(database));
    }

    public Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoutineInfo>>(System.Array.Empty<RoutineInfo>());

    public Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct)
        => Task.FromResult("");

    public Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct)
        => Task.FromResult("");
}

internal sealed class FakeExecutor : IQueryExecutor
{
    private static QueryResult Empty => new(
        System.Array.Empty<ColumnDescriptor>(), System.Array.Empty<object?[]>(),
        0, System.TimeSpan.Zero, null, null, false);

    public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(new[] { Empty });

    public Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
        => Task.FromResult(Empty);

    public Task<long?> CountAsync(string sql, CancellationToken ct)
        => Task.FromResult<long?>(0);

    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(new[] { Empty });
}

/// <summary>An executor whose <see cref="ExecuteAsync"/> blocks (per distinct SQL) until the test releases
/// or the token is cancelled — so two tabs can be held mid-run at once to prove concurrency + per-tab
/// cancellation. Non-blocking for the other operations (not exercised by the concurrency test).</summary>
internal sealed class GatedExecutor : IQueryExecutor
{
    private static QueryResult Empty => new(
        System.Array.Empty<ColumnDescriptor>(), System.Array.Empty<object?[]>(),
        0, System.TimeSpan.Zero, null, null, false);

    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource> _gates = new();

    /// <summary>How many <see cref="ExecuteAsync"/> calls have reached the gate (started executing).</summary>
    public int Started { get { lock (_lock) return _gates.Count; } }

    public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        // Key by the SQL the executor actually receives (the caller may have appended a paging LIMIT).
        TaskCompletionSource gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(sql, out gate!))
            {
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates[sql] = gate;
            }
        }
        using (ct.Register(() => gate.TrySetCanceled(ct)))
            await gate.Task;
        return new[] { Empty };
    }

    /// <summary>Unblock every in-flight run whose SQL contains <paramref name="sqlFragment"/> (the caller
    /// may have appended a paging LIMIT, so match on a fragment of the original text).</summary>
    public void Release(string sqlFragment)
    {
        lock (_lock)
            foreach (var (key, gate) in _gates)
                if (key.Contains(sqlFragment)) gate.TrySetResult();
    }

    public Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct) => Task.FromResult(Empty);
    public Task<long?> CountAsync(string sql, CancellationToken ct) => Task.FromResult<long?>(0);
    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(new[] { Empty });
}

internal sealed class FakeSnapshot : ISchemaSnapshot
{
    public FakeSnapshot(string database) => Database = database;
    public string Database { get; }
    public IReadOnlyList<string> Schemas => System.Array.Empty<string>();
    public IReadOnlyList<TableInfo> Tables => System.Array.Empty<TableInfo>();
    public IReadOnlyList<ColumnInfo> ColumnsOf(long tableId) => System.Array.Empty<ColumnInfo>();
    public TableInfo? ResolveTable(string? schema, string name) => null;
    public IReadOnlyList<ForeignKeyInfo> ForeignKeysTouching(long tableId) => System.Array.Empty<ForeignKeyInfo>();
}

internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<Guid, string> _store = new();
    public bool IsSecure => true;
    public List<Guid> Fetched { get; } = new();

    public Task SetPasswordAsync(Guid id, string password, CancellationToken ct) { _store[id] = password; return Task.CompletedTask; }
    public Task<string?> GetPasswordAsync(Guid id, CancellationToken ct)
    {
        Fetched.Add(id);
        return Task.FromResult(_store.TryGetValue(id, out var p) ? p : null);
    }
    public Task DeleteAsync(Guid id, CancellationToken ct) { _store.Remove(id); return Task.CompletedTask; }
}

/// <summary>Hands back a queued sequence of prompt answers (null = user cancelled). Counts calls.</summary>
internal sealed class FakeCredentialPrompt : ICredentialPrompt
{
    private readonly Queue<string?> _answers;
    public int Calls { get; private set; }
    public FakeCredentialPrompt(params string?[] answers) => _answers = new Queue<string?>(answers);
    public Task<string?> RequestPasswordAsync(ConnectionInfo info, string? message, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
    }
}

/// <summary>Mints tokens from a per-call factory (call index → credential). Counts calls.</summary>
internal sealed class FakeEntraTokens : IEntraTokenProvider
{
    private readonly Func<int, Credential> _factory;
    public int Calls { get; private set; }
    public FakeEntraTokens(Func<int, Credential> factory) => _factory = factory;
    public Task<Credential> GetTokenAsync(ConnectionInfo info, CancellationToken ct)
        => Task.FromResult(_factory(Calls++));
}

/// <summary>Fails if a token is ever requested — for tests whose kind should never hit the token path.</summary>
internal sealed class ThrowingEntraTokens : IEntraTokenProvider
{
    public Task<Credential> GetTokenAsync(ConnectionInfo info, CancellationToken ct)
        => throw new InvalidOperationException("token provider should not be called");
}
