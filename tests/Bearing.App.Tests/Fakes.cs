using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Core.Updates;
using Bearing.Core.Workspace;

namespace Bearing.App.Tests;

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

    /// <summary>When set, every metadata reader's snapshot load blocks on this gate — lets a test hold a
    /// schema load in flight while the session underneath it is replaced (DB switch).</summary>
    public TaskCompletionSource? MetadataGate;

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
        => new FakeMetadata((FakeFactory)factory) { Gate = MetadataGate };

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

    /// <summary>When set, the load waits for the test to release it (see FakeProvider.MetadataGate).</summary>
    public TaskCompletionSource? Gate;

    public async Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        Interlocked.Increment(ref LoadCount);
        if (Gate is not null) await Gate.Task.WaitAsync(ct);
        return new FakeSnapshot(database);
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

    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<long?> CountAsync(string sql, CancellationToken ct)
        => Task.FromResult<long?>(0);

    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(new[] { Empty });
}

/// <summary>An executor that returns a one-column result set — enough for <c>ResultSetBuilder</c> to treat it
/// as pageable — and whose <see cref="CountAsync"/> is scripted: it returns <see cref="CountValue"/>, or
/// throws <see cref="CountError"/> when one is set. Lets the count paths be driven without a live database.</summary>
internal sealed class PageableExecutor : IQueryExecutor
{
    /// <summary>What a successful count reports; null means "this query can't be counted" (shape).</summary>
    public long? CountValue { get; set; }

    /// <summary>When set, <see cref="CountAsync"/> throws it — a real count failure, not an uncountable query.</summary>
    public System.Exception? CountError { get; set; }

    /// <summary>Rows the source query has in total. The default of one keeps the single-page result the
    /// count tests expect; raise it (with <see cref="PageSize"/>) to exercise paging / fetch-all.</summary>
    public int TotalRows { get; set; } = 1;

    /// <summary>Rows a page returns. Set this to the same value as <c>AppSettings.ResultPageSize</c>: the
    /// view-model infers "more rows exist" from a page coming back exactly that full, so a mismatch makes the
    /// fake page in a way the real app never would.</summary>
    public int PageSize { get; set; } = 1;

    /// <summary>How many <see cref="ExecutePageAsync"/> calls have been served (Load more's page count).
    /// Fetch all no longer pages, so this staying at zero is part of what its tests assert.</summary>
    public int PageCalls { get; private set; }

    /// <summary>How many <see cref="StreamRowsAsync"/> calls have been served — fetch all is one.</summary>
    public int StreamCalls { get; private set; }

    /// <summary>The SQL the last stream was asked for, so a test can assert the offset/limit window the
    /// view-model built (it must skip the rows already on screen and stop at the ceiling).</summary>
    public string? LastStreamSql { get; private set; }

    private int _served;

    /// <summary>Delay each page by this many milliseconds — long enough for a test to cancel mid-fetch.</summary>
    public int PageDelayMs { get; set; }

    /// <summary>Invoked with the 1-based page number before each page is served, so a test can cancel a
    /// fetch-all at a known point instead of racing a timer. Cancelling here surfaces exactly as it would
    /// from a real driver: the token check right after this throws.</summary>
    public System.Action<int>? BeforePage { get; set; }

    /// <summary>Invoked with the 1-based batch number before each streamed batch is yielded, so a test can
    /// cancel (or throw) mid-stream at a known point. Same contract as <see cref="BeforePage"/>.</summary>
    public System.Action<int>? BeforeBatch { get; set; }

    private static readonly ColumnDescriptor[] Columns = [new("n", "int4", typeof(int))];

    /// <summary>The next page: rows numbered from where the last one stopped, so a test can assert both the
    /// count and the order of what fetch-all appended.</summary>
    private QueryResult NextPage()
    {
        var take = System.Math.Max(0, System.Math.Min(PageSize, TotalRows - _served));
        var rows = new object?[take][];
        for (var i = 0; i < take; i++) rows[i] = new object?[] { _served + i + 1 };
        _served += take;
        return new QueryResult(Columns, rows, take, System.TimeSpan.Zero, null, null, Truncated: _served < TotalRows);
    }

    public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        _served = 0; // a re-run starts the source query over
        return Task.FromResult<IReadOnlyList<QueryResult>>(new[] { NextPage() });
    }

    /// <summary>When set, every page comes back as a failed result carrying this error — how a real page
    /// failure arrives (the executor catches the driver exception), which is the case Load more has to
    /// recognise instead of reading it as "no more rows".</summary>
    public QueryError? PageError { get; set; }

    public async Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
    {
        PageCalls++;
        BeforePage?.Invoke(PageCalls);
        // Returned ahead of the token check on purpose: a *cancelled* statement also comes back this way
        // (Npgsql raises 57014, the executor turns it into an error result), so a caller can't rely on a
        // cancel throwing out of here. That swallow is exactly what the caller has to cope with.
        if (PageError is not null)
            return new QueryResult(
                System.Array.Empty<ColumnDescriptor>(), System.Array.Empty<object?[]>(),
                0, System.TimeSpan.Zero, null, PageError, Truncated: false);
        if (PageDelayMs > 0) await Task.Delay(PageDelayMs, ct);
        ct.ThrowIfCancellationRequested();
        return NextPage();
    }

    /// <summary>When set, the stream throws this after <see cref="StreamErrorAfterBatches"/> batches — a read
    /// that dies partway (connection dropped, table vanished), which must not read as a complete fetch.</summary>
    public System.Exception? StreamError { get; set; }

    public int StreamErrorAfterBatches { get; set; }

    /// <summary>
    /// Serves the rest of the source query as one execution, in <see cref="QueryOptions.BatchRows"/> batches,
    /// continuing from wherever the last page stopped (the view-model asks for exactly that window with an
    /// OFFSET). Mirrors the real reader loop on the two things fetch-all depends on: rows come out in order
    /// from a single pass, and <see cref="RowBatch.Truncated"/> is set only when
    /// <see cref="QueryOptions.MaxRows"/> cut the read while rows were still waiting.
    /// </summary>
    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        StreamCalls++;
        LastStreamSql = sql;

        var batchSize = System.Math.Max(1, options.BatchRows);
        var batch = new List<object?[]>();
        var streamed = 0;
        var batches = 0;

        while (_served < TotalRows)                 // "the server still has a row"
        {
            if (options.MaxRows is { } max && streamed >= max)
            {
                yield return new RowBatch(batch, Truncated: true);
                yield break;
            }

            batch.Add(new object?[] { ++_served });
            streamed++;
            if (batch.Count < batchSize) continue;

            batches++;
            BeforeBatch?.Invoke(batches);
            if (StreamError is not null && batches > StreamErrorAfterBatches) throw StreamError;
            if (PageDelayMs > 0) await Task.Delay(PageDelayMs, ct);
            ct.ThrowIfCancellationRequested();
            yield return new RowBatch(batch, Truncated: false);
            batch = new List<object?[]>();
        }

        if (batch.Count > 0) yield return new RowBatch(batch, Truncated: false);
    }

    public Task<long?> CountAsync(string sql, CancellationToken ct)
        => CountError is not null ? Task.FromException<long?>(CountError) : Task.FromResult(CountValue);

    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(
            new[] { new QueryResult(Columns, System.Array.Empty<object?[]>(), 0, System.TimeSpan.Zero, null, null, false) });
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

    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<long?> CountAsync(string sql, CancellationToken ct) => Task.FromResult<long?>(0);
    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(new[] { Empty });
}

internal sealed class FakeSnapshot : ISchemaSnapshot
{
    public FakeSnapshot(string database) => Database = database;
    public string Database { get; }
    public IReadOnlyList<string> Schemas => System.Array.Empty<string>();
    public IReadOnlyList<string> SearchPath => System.Array.Empty<string>();
    public IReadOnlyList<TableInfo> Tables => System.Array.Empty<TableInfo>();
    public IReadOnlyList<ColumnInfo> ColumnsOf(long tableId) => System.Array.Empty<ColumnInfo>();
    public TableInfo? ResolveTable(string? schema, string name) => null;
    public IReadOnlyList<ForeignKeyInfo> ForeignKeysTouching(long tableId) => System.Array.Empty<ForeignKeyInfo>();
}

internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<Guid, string> _store = new();
    public bool IsSecure { get; init; } = true;
    public List<Guid> Fetched { get; } = new();

    /// <summary>False models a machine with no reachable keychain: writes are refused and nothing is kept.</summary>
    public bool CanStore { get; init; } = true;

    /// <summary>What the store said when it was rejected — carried to the UI so a warning can explain
    /// itself instead of asserting a cause (see SecretStorageAdvice).</summary>
    public string? UnavailableReason { get; init; }

    public Task SetPasswordAsync(Guid id, string password, CancellationToken ct)
    {
        if (!CanStore) throw new SecretStorageRefusedException("no keyring (fake)");
        _store[id] = password;
        return Task.CompletedTask;
    }

    /// <summary>Seed a secret regardless of <see cref="CanStore"/> — models one written by a keyring that
    /// later went away.</summary>
    public void Seed(Guid id, string password) => _store[id] = password;

    /// <summary>Thrown from <see cref="GetPasswordAsync"/> — a keyring that *errored* rather than answering
    /// "no such item". Distinct from returning null on purpose: the real store used to collapse the two.</summary>
    public Exception? ReadThrows { get; init; }

    public Task<string?> GetPasswordAsync(Guid id, CancellationToken ct)
    {
        Fetched.Add(id);
        if (ReadThrows is not null) throw ReadThrows;
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

/// <summary>
/// Scriptable <see cref="IDialogService"/> for close-prompt tests: the caller decides what the user
/// "chose" and where a Save-As lands, and the fake records what it was asked so a test can assert the
/// prompt did (or did not) appear.
/// </summary>
internal sealed class FakeDialogs : Bearing.App.Services.IDialogService
{
    private readonly Bearing.App.Services.CloseChoice _choice;
    private readonly string? _saveAsPath;

    public FakeDialogs(Bearing.App.Services.CloseChoice choice = Bearing.App.Services.CloseChoice.Discard,
        string? saveAsPath = null)
    {
        _choice = choice;
        _saveAsPath = saveAsPath;
    }

    /// <summary>Tab names the close prompt was raised for, in order.</summary>
    public List<string> ClosePrompts { get; } = new();

    public int SavePickerCalls { get; private set; }

    /// <summary>What the running-query prompt answers. False keeps the query (and the tab/window).</summary>
    public bool CancelRunningAnswer { get; set; } = true;

    /// <summary>Tab names the running-query prompt was raised for; null entries are the quit variant.</summary>
    public List<string?> CancelRunningPrompts { get; } = new();

    public Task<bool> ConfirmCancelRunningAsync(int runningCount, string? tabName = null)
    {
        CancelRunningPrompts.Add(tabName);
        return Task.FromResult(CancelRunningAnswer);
    }

    public Task<Bearing.App.Services.CloseChoice> ConfirmCloseTabAsync(string tabName)
    {
        ClosePrompts.Add(tabName);
        return Task.FromResult(_choice);
    }

    /// <summary>What the delete-script prompt answers. False by default: nothing deletes a file unless a
    /// test says so explicitly.</summary>
    public bool DeleteScriptAnswer { get; set; }

    /// <summary>The file names the delete prompt was raised for, in order.</summary>
    public List<string> DeleteScriptPrompts { get; } = new();

    public Task<bool> ConfirmDeleteScriptAsync(string fileName)
    {
        DeleteScriptPrompts.Add(fileName);
        return Task.FromResult(DeleteScriptAnswer);
    }

    /// <summary>What the remove-project prompt answers. Cancel by default: nothing deletes a folder unless
    /// a test says so explicitly.</summary>
    public Bearing.App.Services.ProjectRemoval RemoveProjectAnswer { get; set; } = Bearing.App.Services.ProjectRemoval.Cancel;

    /// <summary>The (name, directory) pairs the remove-project prompt was raised for, in order.</summary>
    public List<(string Name, string Directory)> RemoveProjectPrompts { get; } = new();

    public Task<Bearing.App.Services.ProjectRemoval> ConfirmRemoveProjectAsync(string name, string directory)
    {
        RemoveProjectPrompts.Add((name, directory));
        return Task.FromResult(RemoveProjectAnswer);
    }

    public Task<string?> PickSaveScriptAsync(string suggestedName, string? startDir)
    {
        SavePickerCalls++;
        return Task.FromResult(_saveAsPath);
    }

    /// <summary>Where the next export picker "lands". Null = the user cancelled the picker.</summary>
    public string? ExportPath { get; set; }

    /// <summary>The (suggested name, format) pairs the export picker was opened with, in order.</summary>
    public List<(string SuggestedName, Bearing.App.Results.ExportFormat Format)> ExportPickers { get; } = new();

    public Task<string?> PickExportFileAsync(string suggestedName, Bearing.App.Results.ExportFormat format)
    {
        ExportPickers.Add((suggestedName, format));
        return Task.FromResult(ExportPath);
    }

    /// <summary>What the write/save confirmation answers. False cancels the write.</summary>
    public bool ConfirmWriteAnswer { get; set; } = true;

    /// <summary>Every write confirmation raised, in order — so a test can assert what the user was shown.</summary>
    public List<Bearing.App.Services.WriteConfirmation> WriteConfirmations { get; } = new();

    public Task<bool> ConfirmWriteAsync(Bearing.App.Services.WriteConfirmation request)
    {
        WriteConfirmations.Add(request);
        return Task.FromResult(ConfirmWriteAnswer);
    }

    public Task<Bearing.App.Views.ConnectionDialogResult?> ShowConnectionDialogAsync(ConnectionInfo? existing, string? existingPassword,
        Func<ConnectionInfo, string?, CancellationToken, Task<bool>> test,
        Bearing.App.Services.SecretStoragePosture storage)
        => Task.FromResult<Bearing.App.Views.ConnectionDialogResult?>(null);
    public Task<string?> ShowTextPromptAsync(string prompt, string initial = "") => Task.FromResult<string?>(null);
    /// <summary>Where the folder picker was told to start, per call — the projects folder, not home.</summary>
    public List<string?> FolderPickerStarts { get; } = new();

    public Task<string?> PickFolderAsync(string title, string? startDir = null)
    {
        FolderPickerStarts.Add(startDir);
        return Task.FromResult<string?>(null);
    }
    public Task<string?> PickOpenScriptAsync(string? startDir) => Task.FromResult<string?>(null);
    public void ShowSqlPreview(string sql, string title = "SQL preview — changes to save") { }
}

/// <summary>
/// In-memory <see cref="IAppSettingsStore"/> that records every write, so a test can assert what actually
/// reached disk (and how often) rather than only what the service holds. Set <see cref="ThrowOnSave"/> to
/// exercise the unwritable-file path.
/// </summary>
internal sealed class FakeSettingsStore : IAppSettingsStore
{
    private AppSettings _settings;

    public FakeSettingsStore(AppSettings? initial = null) => _settings = initial ?? new AppSettings();

    /// <summary>Every value handed to <see cref="Save"/>, in order.</summary>
    public List<AppSettings> Saves { get; } = new();

    public bool ThrowOnSave { get; set; }

    public string Location => "(fake)";

    public AppSettings Load() => _settings;

    public void Save(AppSettings settings)
    {
        if (ThrowOnSave) throw new IOException("disk full");
        Saves.Add(settings);
        _settings = settings;
    }
}

/// <summary>
/// A scriptable <see cref="IUpdateService"/>: decides what a check finds, how the download progresses, and
/// which step (if any) throws. Counts calls so a test can assert that a check did <em>not</em> happen, which
/// is half of what the update policy promises.
/// </summary>
internal sealed class FakeUpdateService : IUpdateService
{
    /// <summary>The version a check reports. Null means "already up to date".</summary>
    public string? Available { get; set; }

    public bool IsSupported { get; set; } = true;

    /// <summary>Set to model a misconfigured updater rather than an absent one.</summary>
    public string? UnavailableReason { get; set; }

    public Exception? CheckThrows { get; set; }
    public Exception? DownloadThrows { get; set; }
    public Exception? ApplyThrows { get; set; }

    /// <summary>Progress values handed to the caller during a download.</summary>
    public int[] ProgressSteps { get; set; } = new[] { 50, 100 };

    public int Checks { get; private set; }
    public int Downloads { get; private set; }

    /// <summary>The update staged for install-on-exit, if <see cref="ApplyOnExit"/> was called.</summary>
    public UpdateCheck? AppliedOnExit { get; private set; }

    /// <summary>The update applied with an immediate relaunch — the path the app must never take.</summary>
    public UpdateCheck? AppliedImmediately { get; private set; }

    public Task<UpdateCheck?> CheckAsync(CancellationToken ct = default)
    {
        Checks++;
        if (CheckThrows is not null) return Task.FromException<UpdateCheck?>(CheckThrows);
        return Task.FromResult(Available is null ? null : new UpdateCheck(Available, Available));
    }

    public Task DownloadAsync(UpdateCheck update, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        Downloads++;
        if (DownloadThrows is not null) return Task.FromException(DownloadThrows);
        foreach (var step in ProgressSteps) progress?.Report(step);
        return Task.CompletedTask;
    }

    public void ApplyOnExit(UpdateCheck update)
    {
        if (ApplyThrows is not null) throw ApplyThrows;
        AppliedOnExit = update;
    }

    public void ApplyAndRestart(UpdateCheck update) => AppliedImmediately = update;
}
