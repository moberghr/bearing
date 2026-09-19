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
}
