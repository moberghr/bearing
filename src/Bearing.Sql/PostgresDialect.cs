namespace Bearing.Sql;

/// <summary>
/// The PostgreSQL dialect: a facade over the Postgres-named statics this project already had, so the
/// engine-neutral callers can go through <see cref="ISqlDialect"/> without any Postgres string being
/// written down twice. Nothing here decides anything — every rule still lives in
/// <see cref="PgIdentifier"/>, <see cref="FirstPageLimiter"/>, <see cref="PageSql"/> and the risky-verb
/// set below, which is exactly the set the guard has always used.
/// </summary>
public sealed class PostgresDialect : ISqlDialect
{
    /// <summary>The shared instance. Stateless, so one is enough; it is also the default every
    /// Postgres-bound static entry point passes to itself.</summary>
    public static PostgresDialect Instance { get; } = new();

    /// <summary>Matches <c>PostgresProvider.ProviderId</c>, which lives in <c>Bearing.Data</c> and so
    /// cannot be referenced from here (§2.2). The pairing is asserted by the App layer's registry.</summary>
    public string Id => "postgres";

    public string Quote(string identifier) => PgIdentifier.Quote(identifier);
    public string QuoteIfNeeded(string identifier) => PgIdentifier.QuoteIfNeeded(identifier);
    public bool NeedsQuoting(string identifier) => PgIdentifier.NeedsQuoting(identifier);
    public string Unquote(string identifier) => PgIdentifier.Unquote(identifier);

    public string? TryAppendPage(string sql, int offset, int limit)
        => FirstPageLimiter.TryAppendPage(sql, offset, limit);

    /// <summary>Never null: Postgres accepts a CTE, a locking clause and everything else this project
    /// generates inside a derived table, so there is no shape to refuse.</summary>
    public string? Wrap(string sql, int offset, int limit) => PageSql.Wrap(sql, offset, limit);

    /// <summary>Never null, for the same reason as <see cref="Wrap"/>.</summary>
    public string? CountWrap(string sql) => PageSql.CountWrap(sql);

    /// <summary>RETURNING is a trailing clause, so the statement reads in the order it was built.
    /// Postgres has no equivalent of SQL Server's trigger restriction, so the no-returning form exists
    /// only for symmetry — nothing here ever needs to retry with it.</summary>
    public string InsertStatement(string qualifiedTable, string columnList, string valueList, bool withReturning)
        => $"insert into {qualifiedTable} ({columnList}) values ({valueList})"
           + (withReturning ? " returning *" : "");

    /// <summary>RETURNING trails the statement here too, so the read-back reads last.</summary>
    public string UpdateStatement(
        string qualifiedTable, string setList, string whereClause, IReadOnlyList<string>? returningColumns)
        => $"update {qualifiedTable} set {setList} where {whereClause}"
           + (returningColumns is { Count: > 0 } cols ? " returning " + string.Join(", ", cols) : "");

    /// <summary>The Postgres table, unchanged — <see cref="EditExpression"/> was written against this
    /// engine and keeps its own entry points for the callers (and tests) that name it directly.</summary>
    public string? TryEditExpression(string? text) => EditExpression.TryRecognize(text);

    /// <inheritdoc cref="ISqlDialect.EditExpressions"/>
    public IReadOnlyCollection<string> EditExpressions => EditExpression.All;

    /// <inheritdoc cref="ISqlDialect.OfferedEditExpressions"/>
    public IReadOnlyList<string> OfferedEditExpressions => EditExpression.Offered;

    /// <summary>True: <see cref="WriteGuard"/> is built on the vendored PostgreSQL lexer, so it reads
    /// this engine's batches for real and may report a plain SELECT as safe.</summary>
    public bool HasDialectAwareGuard => true;

    /// <summary>The vendored PostgreSQL lexer, which is what these two have always used — so neither
    /// answer changes for this engine by the questions becoming per-dialect.</summary>
    public bool InStringLiteral(string sql, int offset) => SqlStringLiterals.Contains(sql, offset);

    /// <inheritdoc cref="InStringLiteral"/>
    public string RedactLiterals(string? sql) => SqlRedactor.Redact(sql);

    /// <summary>The vendored PostgreSQL lexer, which reads this engine for real — unchanged from
    /// before a second dialect existed.</summary>
    public IReadOnlyList<StatementRisk> DescribeStatements(string sql)
        => WriteGuard.DescribeWithPostgresLexer(sql, this);

    /// <summary>
    /// Postgres' transaction vocabulary. <c>BEGIN</c> and <c>START TRANSACTION</c> open one; <c>COMMIT</c>
    /// and <c>END</c> close it (<c>END</c> is a synonym here, unlike T-SQL where it closes a block);
    /// <c>ROLLBACK</c>, <c>ABORT</c>, <c>SAVEPOINT</c> and <c>RELEASE</c> are the rest of it.
    /// <para>
    /// <c>PREPARE</c> needs its second word: <c>PREPARE TRANSACTION</c> is two-phase commit, while a bare
    /// <c>PREPARE</c> is a prepared statement and has nothing to do with transactions.
    /// </para>
    /// </summary>
    public string? TransactionControl(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return null;
        var first = words[0].ToUpperInvariant();
        if (first == "PREPARE")
            return words.Count > 1 && words[1].Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)
                ? "PREPARE TRANSACTION"
                : null;
        return TransactionVerbs.Contains(first) ? first : null;
    }

    private static readonly HashSet<string> TransactionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "START", "COMMIT", "END", "ROLLBACK", "ABORT", "SAVEPOINT", "RELEASE",
    };

    /// <summary>The same lexer again, and the same split the editor has always had: semicolons and blank
    /// lines, with dollar-quoted bodies and comments read for what they are.</summary>
    public IReadOnlyList<StatementSpan> SplitStatements(string sql)
        => StatementSplitter.SplitWithPostgresLexer(sql);

    /// <summary>The vendored PostgreSQL grammar, behind the same forwarding arrangement as everything
    /// else here: <see cref="PgParseRules"/> holds no rules of its own, it points at
    /// <see cref="PgCompletionRules"/> and <see cref="PgParsing"/>.</summary>
    public ISqlParseRules ParseRules => PgParseRules.Instance;

    /// <summary>
    /// The verbs the guard has always treated as writes. Deliberately unchanged by the arrival of a
    /// second engine: T-SQL's extra verbs live on <see cref="SqlServerDialect"/>, because adding them
    /// here would silently change what Postgres confirms on (Postgres' own <c>EXECUTE</c> of a prepared
    /// write is a real gap, but closing it is a behaviour change, not this refactor).
    /// </summary>
    public IReadOnlySet<string> RiskyVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Data writes.
        "INSERT", "UPDATE", "DELETE", "MERGE", "COPY",
        // Schema / object DDL (CREATE covers CREATE TABLE … AS SELECT; REFRESH covers materialized views).
        "CREATE", "DROP", "TRUNCATE", "ALTER", "REFRESH",
        // Privilege changes.
        "GRANT", "REVOKE",
        // Procedural blocks that can write arbitrarily.
        "CALL", "DO",
    };

    /// <summary>The reads a host outside Bearing may send. Postgres answers <c>SHOW</c> for a setting,
    /// <c>TABLE t</c> as shorthand for <c>SELECT * FROM t</c>, and <c>VALUES</c> as a standalone row
    /// constructor; all three are reads and all three are useful to an agent exploring a database.</summary>
    public IReadOnlySet<string> ExternalReadVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "EXPLAIN", "SHOW", "TABLE", "VALUES",
    };

    /// <summary>
    /// Postgres functions an exposed connection refuses, grouped by what they reach past.
    /// <para>
    /// Every one of these is callable from a plain <c>SELECT</c> and most are unaffected by
    /// <c>default_transaction_read_only</c> — <c>pg_terminate_backend</c> is a signal rather than a write
    /// (§9.13), and <c>pg_read_file</c> is a read, just not of the data. Measured on a superuser
    /// connection: <c>select pg_read_file('/etc/passwd')</c> returned the file with read-only fully in
    /// force. So this list is not redundant with the read-only session; it covers a different axis.
    /// </para>
    /// </summary>
    /// <inheritdoc/>
    public bool SupportsExplainPlan => true;

    public IReadOnlySet<string> ExternalDeniedFunctions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Other people's sessions.
        "pg_terminate_backend", "pg_cancel_backend",
        // The server's filesystem.
        "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_stat_file", "pg_ls_logdir", "pg_ls_waldir",
        // Large objects — lo_import/lo_export are file I/O wearing a data-type costume.
        "lo_import", "lo_export", "lo_unlink",
        // This session's own settings. set_config is SET in SELECT clothing, so the allow-list above
        // cannot see it — and it is the exact escape that would shed a role the startup packet set.
        "set_config",
        // The server's configuration and log files.
        "pg_reload_conf", "pg_rotate_logfile", "pg_stat_reset",
    };

    /// <inheritdoc/>
    /// <remarks>
    /// The three catalogs §1.8 names, and for its reasons. They are not hypothetical on this path: an
    /// exposed connection admits <c>select * from pg_user_mappings</c> as the plain read it is, and
    /// <c>umoptions</c> holds a foreign server's password in cleartext for the mapping's owner — no
    /// superuser needed. <c>pg_authid.rolpassword</c> and <c>pg_subscription.subconninfo</c> do need one,
    /// which is precisely the over-privileged connection this fence is for.
    /// <para>
    /// <c>pg_shadow</c> is <c>pg_authid</c> under another name and has to be listed separately; the
    /// <c>pg_roles</c> view is deliberately <b>not</b> here, because masking the hash is what it is for
    /// (§1.7) and it is how anyone should read roles.
    /// </para>
    /// </remarks>
    public IReadOnlySet<string> ExternalDeniedRelations { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pg_authid", "pg_shadow",
        "pg_subscription",
        "pg_user_mappings", "pg_user_mapping",
    };

    /// <inheritdoc/>
    public (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned) ExternalNameScan(string statement)
    {
        var tokens = PgParsing.LexAll(statement);
        return (SqlNameScan.CalledNames(tokens), SqlNameScan.MentionedNames(tokens));
    }
}
