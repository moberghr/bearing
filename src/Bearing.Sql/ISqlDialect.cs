namespace Bearing.Sql;

/// <summary>
/// Everything about generated SQL that changes with the engine: how an identifier is quoted, how a page
/// and a row count are expressed, how an INSERT asks for the row it just wrote, and how far the write
/// guard can be trusted.
/// <para>
/// Deliberately text-only — no connection, no driver types, no <c>Bearing.Data</c> — so
/// <c>Bearing.Sql</c> keeps depending on <c>Core</c> alone (§2.2) and every rule here is unit-testable
/// as a string. The engine-facing half of the same split lives behind <c>IDbProvider</c>; a provider and
/// its dialect share an <see cref="Id"/> so the App layer can pair them and the *selected connection's*
/// dialect is the one that shapes its SQL.
/// </para>
/// <para>
/// The Postgres-named statics in this project (<see cref="PgIdentifier"/>, <see cref="PageSql"/>,
/// <see cref="FirstPageLimiter"/>, <see cref="DmlGenerator"/>, <see cref="TableDdlGenerator"/>,
/// <see cref="WriteGuard"/>) are the Postgres-bound entry points for callers that have no dialect to
/// hand; <see cref="PostgresDialect"/> delegates to exactly those, so there is one implementation of
/// each string, not two.
/// </para>
/// </summary>
public interface ISqlDialect
{
    /// <summary>Matches the <c>IDbProvider.Id</c> of the engine this dialect writes for.</summary>
    string Id { get; }

    // ---- Identifiers ----

    /// <summary>Unconditionally quote, escaping the closing delimiter. For generated SQL nobody reads.</summary>
    string Quote(string identifier);

    /// <summary>Quote only when the bare form would not round-trip — the completion-facing form, since
    /// that output is read and typed over.</summary>
    string QuoteIfNeeded(string identifier);

    /// <summary>True when the bare identifier would not mean itself (folding, illegal characters, or a
    /// keyword the parser will not read as a name).</summary>
    bool NeedsQuoting(string identifier);

    /// <summary>Strip the engine's quoting and unescape, the inverse of <see cref="Quote"/>.</summary>
    string Unquote(string identifier);

    // ---- Paging and counting ----

    /// <summary>
    /// <paramref name="sql"/> with a top-level page clause appended, or <c>null</c> when this statement
    /// cannot safely take one and the caller should fall back to <see cref="Wrap"/>. Preferred over the
    /// wrap because the query's own <c>ORDER BY</c> then governs every page.
    /// </summary>
    string? TryAppendPage(string sql, int offset, int limit);

    /// <summary>
    /// Fallback paging: <paramref name="sql"/> as a derived table, paged from the outside — or
    /// <c>null</c> when this statement cannot legally sit inside one, in which case the caller must
    /// stop offering to page rather than run SQL the server will reject. T-SQL forbids a CTE, a query
    /// hint and a <c>FOR JSON/XML</c> clause inside a derived table; Postgres allows all three, so its
    /// dialect never refuses.
    /// </summary>
    string? Wrap(string sql, int offset, int limit);

    /// <summary>The <c>select count(*) from (…)</c> shape for a total-row count over an arbitrary
    /// query, or <c>null</c> when the statement cannot sit in a derived table at all — the same
    /// refusal <see cref="Wrap"/> makes, and for the same reason. A caller that gets null reports the
    /// total as unavailable instead of asking the server a question it cannot parse.</summary>
    string? CountWrap(string sql);

    // ---- DML ----

    /// <summary>
    /// One INSERT that also returns the row it wrote (so generated keys and defaults refill the grid).
    /// A whole-statement hook rather than a trailing clause because the clause's *position* is dialect
    /// varying: Postgres ends with <c>returning *</c>, T-SQL puts <c>output inserted.*</c> in front of
    /// <c>values</c>, where a trailing OUTPUT would be a syntax error.
    /// </summary>
    /// <param name="qualifiedTable">Already quoted and schema-qualified by this dialect.</param>
    /// <param name="columnList">Already quoted, comma-separated.</param>
    /// <param name="valueList">Parameter placeholders, comma-separated.</param>
    /// <param name="withReturning">When false, emit a plain INSERT with no clause that returns the
    /// written row. The generator asks for both forms so an executor can retry without it — SQL Server
    /// rejects <c>OUTPUT</c> on a table with an enabled trigger, and an insert must not become
    /// impossible because the table is audited.</param>
    string InsertStatement(string qualifiedTable, string columnList, string valueList, bool withReturning);

    // ---- Write guard ----

    /// <summary>
    /// True when <see cref="WriteGuard"/> actually understands this engine's grammar. False makes the
    /// guard fail safe: every statement in the batch is reported risky, labelled so the confirmation can
    /// say the guard is being conservative rather than implying the user's SELECT is destructive.
    /// Never flip this to true for an engine whose statements the lexer cannot split and read (§1.2).
    /// </summary>
    bool HasDialectAwareGuard { get; }

    /// <summary>Leading keywords that mean a statement writes data or alters schema.</summary>
    IReadOnlySet<string> RiskyVerbs { get; }

    /// <summary>
    /// Split a batch into statements and classify each, in <b>this engine's</b> lexical rules. The
    /// guard is a keyword scan, so the lexing is the whole ballgame: a lexer that cannot see a
    /// delimited identifier reads the words inside one as clause keywords, and a lexer that does not
    /// know an engine's batch separator merges statements. Both mis-read a write.
    /// <para>
    /// <see cref="WriteGuard"/> is the caller and still owns the safety net: when
    /// <see cref="HasDialectAwareGuard"/> is false it re-tags whatever comes back as risky, so a
    /// provider that ships without a trustworthy scanner cannot under-report (§1.2).
    /// </para>
    /// </summary>
    IReadOnlyList<StatementRisk> DescribeStatements(string sql);
}
