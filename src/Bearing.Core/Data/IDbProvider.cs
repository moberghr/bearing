using Bearing.Core.Schema;

namespace Bearing.Core.Data;

/// <summary>
/// One per database engine. The registry key for pluggable engines (Postgres in v1; SQL Server /
/// MySQL / DuckDB / SQLite later). Everything DB-touching hangs off this so the rest of the app
/// stays engine-agnostic.
/// </summary>
public interface IDbProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Fields the connect dialog renders for this engine.</summary>
    IReadOnlyList<ConnectionField> ConnectionFields { get; }

    /// <summary>
    /// Whether <see cref="ConnectionInfo.ReadOnly"/> reaches the <b>server</b> on this engine — that is,
    /// whether the server itself refuses the write, or only Bearing does.
    /// <para>
    /// Postgres carries <c>default_transaction_read_only=on</c> in the startup packet, so it catches what a
    /// lexer cannot: a function that writes, dynamic SQL, <c>COPY … TO</c> (#99). SQL Server has no
    /// equivalent — <c>ApplicationIntent=ReadOnly</c> routes to a readable secondary, it does not refuse a
    /// write — so there the client-side refusal is the whole of it, and what stops a write the lexer misses
    /// is a role without write privileges.
    /// </para>
    /// <para>
    /// A flag rather than silence because the difference is one the user is told about: the connection
    /// dialog's note says "the server refuses writes on this connection", which is a claim about *their*
    /// server and must not be made where nobody arranged it (§1.1). The setting still works on both engines
    /// — it is the sentence describing it that changes.
    /// </para>
    /// </summary>
    bool EnforcesReadOnlyOnServer { get; }

    /// <summary>
    /// Whether this engine has a per-session time zone for <see cref="ConnectionInfo.SessionTimeZone"/> to
    /// set (#163). Postgres does — <c>TimeZone</c>, sent in the startup packet. SQL Server does not:
    /// <c>SYSDATETIMEOFFSET()</c> and <c>AT TIME ZONE</c> read the server OS's zone or an explicit one, and
    /// nothing a client sends changes that. A flag rather than a silently ignored field, because the dialog
    /// offering the setting there would be a claim that it does something.
    /// </summary>
    bool SupportsSessionTimeZone { get; }

    /// <summary>Whether this engine can authenticate as the OS identity
    /// (<see cref="CredentialKind.Integrated"/>). The dialog offers that credential kind only where it
    /// works, rather than knowing per-engine which ones have it.</summary>
    bool SupportsIntegratedAuth { get; }

    /// <summary>
    /// Whether this engine's connection factory can actually authenticate with a short-lived Entra access
    /// token (<see cref="CredentialKind.EntraToken"/>) — that is, whether the token has somewhere to go.
    /// <para>
    /// A separate flag from <see cref="SupportsIntegratedAuth"/> for the same reason that one exists: the
    /// dialog must not offer a credential kind the factory cannot honour, rather than offering it and
    /// failing at login. What the flag asks is not "does this engine's cloud support Entra" but "does this
    /// factory put the token somewhere the driver will read it", and the two drivers differ: Npgsql takes it
    /// as the password, so Postgres needed no code at all, while SqlClient takes it only on the connection
    /// object and so needed a path of its own. Both are true today; a third engine's driver may want a
    /// third arrangement, or none.
    /// </para>
    /// </summary>
    bool SupportsEntraToken { get; }

    /// <summary>
    /// Place a failed statement's error (see <see cref="QueryError.SqlState"/>) on the neutral
    /// <see cref="DbErrorKind"/> scale. The engine's codes never leave the provider: the App layer used
    /// to sniff Postgres SQLSTATEs as strings, which quietly mislabelled every other engine's errors.
    /// </summary>
    DbErrorKind Classify(QueryError error);

    /// <summary>
    /// The same judgement for a thrown exception rather than a returned error — what the connect path
    /// has, since a failed handshake never produces a <see cref="QueryError"/>. Implementations should
    /// walk the inner-exception chain: drivers wrap the typed error more often than not.
    /// </summary>
    DbErrorKind ClassifyException(Exception exception);

    IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password);
    IMetadataReader CreateMetadataReader(IDbConnectionFactory factory);
    IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory);

    /// <summary>
    /// A transaction that outlives the call that opened it, for manual-commit mode (#131). Nothing is opened
    /// until <see cref="ITransactionScope.BeginAsync"/>.
    /// <para>
    /// This exists because <see cref="CreateQueryExecutor"/>'s product cannot serve it: every method on that
    /// executor opens its own pooled connection and hands it back when the method returns, so a transaction
    /// begun in one call is over before the next one starts. A scope holds <b>one</b> physical connection for
    /// as long as the user leaves the transaction open, and hands out an <see cref="IQueryExecutor"/> pinned
    /// to it — so nothing downstream has to learn a second way to run a statement.
    /// </para>
    /// </summary>
    ITransactionScope CreateTransactionScope(IDbConnectionFactory factory);

    /// <summary>
    /// Whether this engine can answer <see cref="CreateServerActivity"/> at all.
    /// <para>
    /// A flag rather than an empty result, which is the pattern the four catalog kinds on
    /// <see cref="IMetadataReader"/> use, because activity has no honest empty: an empty
    /// <see cref="ServerActivity"/> makes the panel say "No sessions on this server", and the
    /// <c>SeesAllSessions: false</c> form makes it blame the reading role for not seeing them. Both are
    /// statements about a server nobody asked. A provider that returns false here is saying the feature is
    /// unimplemented for the engine, which is a different sentence and the true one.
    /// </para>
    /// </summary>
    bool SupportsServerActivity { get; }

    /// <summary>The server's own sessions, and the two actions on one (#101). Separate from
    /// <see cref="IMetadataReader"/> because that one is read-only by construction — see
    /// <see cref="IServerActivity"/>. Only meaningful when <see cref="SupportsServerActivity"/>.</summary>
    IServerActivity CreateServerActivity(IDbConnectionFactory factory);
}

/// <summary>Opens/pools underlying connections; hides the concrete ADO.NET driver.</summary>
public interface IDbConnectionFactory : IAsyncDisposable
{
    Task<bool> TestConnectionAsync(CancellationToken ct);
}

public interface IMetadataReader
{
    Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct);

    /// <summary>Bulk catalog read → an immutable snapshot the completion engine can query cheaply.</summary>
    Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct);

    /// <summary>Stored routines (functions/procedures/…) in the reader's database, for schema browsing.</summary>
    Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct);

    /// <summary>Rendered SQL of a view / materialized view, by its table id (<see cref="TableInfo.Id"/>).</summary>
    Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct);

    /// <summary>
    /// Constraints, indexes and triggers of one relation, read on demand (§4.6 of the tree: a table is
    /// expanded far less often than a keystroke happens). Deliberately not part of
    /// <see cref="ISchemaSnapshot"/> — see <see cref="TableDetails"/>.
    /// </summary>
    Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct);

    /// <summary>
    /// Every relation's size in the connected database, keyed by table id (#76).
    /// <para>
    /// One read for the whole database rather than one per table: the sizes come from a single
    /// <c>pg_class</c> join, so fetching them a table at a time would be strictly worse — and the tree wants
    /// them all at once anyway, to sort by them.
    /// </para>
    /// <para>
    /// Not part of <see cref="ISchemaSnapshot"/>, for the same reason as <see cref="TableDetails"/> and one
    /// more: sizes are volatile, so caching them next to structure that is not would make the snapshot stale
    /// in a way nothing else there is. <c>pg_total_relation_size</c> also stats files per relation, which is
    /// not free on a large database — this must never be on the path that renders the tree.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct);

    /// <summary>
    /// Every database's size on the server. A size that cannot be read comes back null rather than throwing:
    /// <c>pg_database_size</c> raises for a database the user cannot connect to, and one inaccessible database
    /// must not cost the sizes of the rest.
    /// </summary>
    Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct);

    /// <summary>Rendered <c>CREATE … FUNCTION/PROCEDURE</c> source, by routine id (<see cref="RoutineInfo.Id"/>).</summary>
    Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct);

    /// <summary>
    /// The per-database object kinds the schema explorer shows beside relations and routines (#119) —
    /// sequences, user-defined types, installed extensions and row-level security policies.
    /// <para>
    /// One call rather than four, and deliberately: they are all wanted at the same moment (the tree has
    /// just been handed a database's relations) and four catalog reads on four round trips would be strictly
    /// worse than four on one connection. It is also what keeps a new engine's cost to one method — a
    /// provider that has no answer for a kind returns an empty list for it, rather than the interface
    /// growing a method it will have to stub.
    /// </para>
    /// <para>
    /// Not part of <see cref="ISchemaSnapshot"/>, for <see cref="TableDetails"/>'s reason: the snapshot is on
    /// the completion hot path and none of this answers a question a keystroke asks.
    /// </para>
    /// </summary>
    Task<DatabaseObjectKinds> GetDatabaseObjectsAsync(CancellationToken ct);

    /// <summary>
    /// Every role on the <b>server</b> (#120) — cluster-wide, so any reachable database can answer it.
    /// <para>
    /// Read from the view that masks the password (<c>pg_roles</c> on Postgres), never the table that holds
    /// it. A provider with no notion of roles returns an empty list.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct);

    /// <summary>
    /// What <paramref name="roleName"/> may do on the reader's own database (#120): the database-level
    /// privileges and the per-relation grants, rendered readably.
    /// <para>
    /// Per database because that is the only scope the answer has — the roles are the server's, but a grant
    /// is on an object, and objects live in one database. Reports whether the reading role could see the
    /// answer at all rather than returning a bare empty list.
    /// </para>
    /// </summary>
    Task<RoleGrants> GetRoleGrantsAsync(string roleName, CancellationToken ct);

    /// <summary>
    /// Tablespaces — the other cluster-wide kind besides roles, so it belongs beside them on the server
    /// rather than under a database. Location and size where the reading role may see them.
    /// </summary>
    Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(CancellationToken ct);
}

public interface IQueryExecutor
{
    /// <summary>
    /// Runs the SQL — which may contain several statements separated by <c>;</c> — and returns one
    /// <see cref="QueryResult"/> per result set (SELECTs and RETURNING clauses produce grids;
    /// INSERT/UPDATE/DDL produce a rows-affected message). Always at least one element; a failure
    /// is returned as a single error result rather than thrown.
    /// </summary>
    Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct);

    /// <summary>
    /// Run one already-built page query (the caller shaped the LIMIT/OFFSET — see <c>PageSql.Page</c>)
    /// as a single row-returning statement. One result set, uncapped; base-table origin isn't read
    /// (the columns come from the first page). The executor only runs the SQL — it doesn't shape it.
    /// </summary>
    Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct);

    /// <summary>
    /// Run one already-built row-returning query (the caller shaped any LIMIT/OFFSET — see <c>PageSql</c>)
    /// and yield its rows in <see cref="QueryOptions.BatchRows"/>-sized batches as the reader drains them,
    /// so a large result materializes incrementally instead of in one jump.
    /// <para>
    /// <b>One statement, one snapshot.</b> This is what "fetch all" runs instead of walking pages: every row
    /// comes from the same execution, so a concurrent insert or delete can't shift rows between pages and
    /// make the fetch duplicate or skip them — and the server produces each row once rather than re-running
    /// the query per page with a growing OFFSET.
    /// </para>
    /// <see cref="QueryOptions.MaxRows"/> caps what is yielded and sets <see cref="RowBatch.Truncated"/> on
    /// the final batch when the server still had rows. Base-table origin isn't read (the columns come from
    /// the first page). Failures are <em>thrown</em>, not returned as an error result: a half-consumed stream
    /// has no single result to carry one, and a caller that must not act on half an answer needs the throw.
    /// </summary>
    IAsyncEnumerable<RowBatch> StreamRowsAsync(string sql, QueryOptions options, CancellationToken ct);

    /// <summary>
    /// Run one already-built count query (the caller shaped the <c>select count(*) from (…)</c> wrapper —
    /// see <c>ISqlDialect.CountWrap</c>) and return its scalar. As with <see cref="ExecutePageAsync"/>, the
    /// executor only runs the SQL — it doesn't shape it: the wrapper is dialect-varying text (SQL Server's
    /// needs an <c>OFFSET 0 ROWS</c> repair before a derived table may carry an <c>ORDER BY</c>), and
    /// generating it here would put a second copy of that text next to every driver.
    /// <para>
    /// Null means the query's <em>shape</em> can't be counted (a multi-statement batch, a non-SELECT, a
    /// data-modifying CTE, or — on SQL Server — a derived table with an unnamed or duplicated column) and
    /// the caller should simply show no total. A real failure — connection lost, table dropped, permission
    /// denied, timeout, cancellation — is <em>thrown</em>, never reported as a missing total, so the UI can
    /// say the count failed instead of silently leaving the row count blank.
    /// </para>
    /// </summary>
    Task<long?> CountAsync(string countSql, CancellationToken ct);

    /// <summary>
    /// Run one or more generated writes (UPDATE/DELETE/INSERT) in a single transaction. Returns one
    /// <see cref="QueryResult"/> per command in order (INSERT … RETURNING yields rows; UPDATE/DELETE
    /// yield an affected-rows message). Any failure rolls back the whole batch and is returned as a
    /// single error result rather than thrown.
    /// </summary>
    Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct);
}

/// <summary>Where a transaction opened by <see cref="ITransactionScope"/> stands.</summary>
public enum TransactionState
{
    /// <summary>Nothing is open — before <see cref="ITransactionScope.BeginAsync"/>, and after either verb.</summary>
    None,

    /// <summary>Open, and able to be committed.</summary>
    Active,

    /// <summary>
    /// Open, but a statement in it failed, so only <see cref="ITransactionScope.RollbackAsync"/> is left.
    /// <para>
    /// On Postgres this is simply what happened: any error aborts the transaction and every later statement
    /// comes back <c>25P02</c> until it ends. On SQL Server it is <b>conservative</b> — some errors there
    /// (a duplicate key, say) leave a transaction the server would still commit — and deliberately so: the
    /// cost is a rollback of work the server would have taken, which the user is told about, against a
    /// <c>COMMIT</c> that Postgres would silently turn into a <c>ROLLBACK</c>. Bearing does not offer to
    /// commit what it cannot promise to commit.
    /// </para>
    /// </summary>
    Aborted,
}

/// <summary>
/// One transaction held open across calls, on one physical connection (#131). Created by
/// <see cref="IDbProvider.CreateTransactionScope"/>; disposing it rolls back anything still open.
/// </summary>
public interface ITransactionScope : IAsyncDisposable
{
    TransactionState State { get; }

    /// <summary>How many statements have run inside this transaction — what the status chip counts.</summary>
    int StatementCount { get; }

    /// <summary>When something last ran in it, for the idle clocks in <see cref="CommitPolicy"/>. Bumped by
    /// the pinned executor, so it measures the transaction's own activity and not the tab's.</summary>
    DateTime LastActivityUtc { get; }

    /// <summary>
    /// Runs on this scope's held connection and transaction. Same interface as the pooled one on purpose:
    /// the caller picks which executor a tab gets and nothing further down knows the difference.
    /// <para>
    /// One difference in behaviour, and it is the point:
    /// <see cref="IQueryExecutor.ExecuteWriteAsync"/> here does <b>not</b> commit — the batch joins the open
    /// transaction and waits for the user like everything else.
    /// </para>
    /// </summary>
    IQueryExecutor Executor { get; }

    /// <summary>Open the transaction. Called once, before anything runs on <see cref="Executor"/>.</summary>
    Task BeginAsync(CancellationToken ct);

    /// <summary>Commit and release the connection. Refused when <see cref="State"/> is
    /// <see cref="TransactionState.Aborted"/> — see that member for why.</summary>
    Task CommitAsync(CancellationToken ct);

    /// <summary>Roll back and release the connection. Always available while anything is open.</summary>
    Task RollbackAsync(CancellationToken ct);
}

/// <summary>Registry of available providers, resolved by <see cref="IDbProvider.Id"/>.</summary>
public interface IProviderRegistry
{
    IDbProvider Get(string providerId);
    IReadOnlyCollection<IDbProvider> All { get; }
}
