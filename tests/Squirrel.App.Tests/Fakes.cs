using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
        return new FakeFactory { TestResult = TestResult, TestThrows = TestThrows };
    }

    public string? LastPassword;

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory) => new FakeMetadata((FakeFactory)factory);
    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory) => new FakeExecutor();
}

internal sealed class FakeFactory : IDbConnectionFactory
{
    public bool TestResult = true;
    public Exception? TestThrows;
    public int DisposeCount;

    public Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        if (TestThrows is not null) throw TestThrows;
        return Task.FromResult(TestResult);
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

    public Task<IReadOnlyList<PgRoutine>> GetRoutinesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PgRoutine>>(System.Array.Empty<PgRoutine>());

    public Task<string> GetViewDefinitionAsync(uint relOid, CancellationToken ct)
        => Task.FromResult("");

    public Task<string> GetRoutineDefinitionAsync(uint routineOid, CancellationToken ct)
        => Task.FromResult("");
}

internal sealed class FakeExecutor : IQueryExecutor
{
    private static QueryResult Empty => new(
        System.Array.Empty<ColumnDescriptor>(), System.Array.Empty<object?[]>(),
        0, System.TimeSpan.Zero, null, null, false);

    public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(new[] { Empty });

    public Task<QueryResult> ExecutePageAsync(string sql, int offset, int limit, CancellationToken ct)
        => Task.FromResult(Empty);

    public Task<long?> CountAsync(string sql, CancellationToken ct)
        => Task.FromResult<long?>(0);

    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(new[] { Empty });

    public async IAsyncEnumerable<ResultBatch> StreamAsync(string sql, QueryOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}

internal sealed class FakeSnapshot : ISchemaSnapshot
{
    public FakeSnapshot(string database) => Database = database;
    public string Database { get; }
    public IReadOnlyList<string> Schemas => System.Array.Empty<string>();
    public IReadOnlyList<PgTable> Tables => System.Array.Empty<PgTable>();
    public IReadOnlyList<PgColumn> ColumnsOf(uint tableOid) => System.Array.Empty<PgColumn>();
    public PgTable? ResolveTable(string? schema, string name) => null;
    public IReadOnlyList<PgForeignKey> ForeignKeysTouching(uint tableOid) => System.Array.Empty<PgForeignKey>();
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
