namespace Bearing.Sql;

/// <summary>
/// Everything about generated SQL that changes with the engine: how an identifier is quoted, how a page
/// and a row count are expressed, how an INSERT asks for the row it just wrote, and how far the write
/// guard can be trusted.
/// <para>
/// Deliberately text-only — no connection, no driver types, no <c>Bearing.Data</c>; the lone
/// exception is <see cref="ParseRules"/>, which hands out this engine's grammar and says why — so
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

    /// <summary>
    /// One UPDATE that also reads back the columns it wrote, for the same reason
    /// <see cref="InsertStatement"/> does — and a whole-statement hook for the same reason too: the
    /// clause's <em>position</em> varies. Postgres ends with <c>returning c1, c2</c>; T-SQL puts
    /// <c>output inserted.c1, inserted.c2</c> between the SET list and the WHERE, where a trailing one
    /// would be a syntax error.
    /// </summary>
    /// <param name="qualifiedTable">Already quoted and schema-qualified by this dialect.</param>
    /// <param name="setList">Already quoted assignments, comma-separated.</param>
    /// <param name="whereClause">The predicate, without the <c>where</c> keyword.</param>
    /// <param name="returningColumns">Already-quoted column identifiers to read back, or null for none.
    /// The generator asks for both forms so an executor can retry without the clause — SQL Server rejects
    /// <c>OUTPUT</c> without <c>INTO</c> on a table with an enabled trigger for an UPDATE exactly as it
    /// does for an INSERT.</param>
    string UpdateStatement(
        string qualifiedTable, string setList, string whereClause, IReadOnlyList<string>? returningColumns);

    /// <summary>
    /// The canonical SQL this engine spells a recognised inline-edit expression with — <c>now()</c> on
    /// Postgres, <c>getdate()</c> on SQL Server — or null when the text is not one of them (#149).
    /// <para>
    /// Per-dialect because the table is engine-specific vocabulary, and a wrong answer here is visible
    /// twice over: the cell is drawn as an expression the server will evaluate, and the save emits SQL
    /// that engine cannot run. <c>current_timestamp</c> and <c>default</c> happen to be common to both;
    /// <c>gen_random_uuid()</c> and <c>newid()</c> are each meaningless to the other.
    /// </para>
    /// <para>
    /// The string comes from the dialect's own table, never from the caller — that is what keeps §5.4's
    /// one break bounded (<see cref="EditExpression"/>).
    /// </para>
    /// </summary>
    string? TryEditExpression(string? text);

    /// <summary>Every expression this dialect accepts in a cell, canonically spelled — for anything that
    /// has to <em>list</em> them (a menu, a tooltip, documentation) rather than test one.</summary>
    IReadOnlyCollection<string> EditExpressions { get; }

    /// <summary>
    /// The subset of <see cref="EditExpressions"/> a menu offers, in menu order. Shorter on purpose:
    /// a cell accepts anything in the table because the user typed it and meant it, while a menu item
    /// claims "this works here" — so an expression needing an extension that may not be installed, or one
    /// differing from its neighbour in a way a menu cannot explain, stays typeable and unadvertised.
    /// </summary>
    IReadOnlyList<string> OfferedEditExpressions { get; }

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
    /// Leading keywords this engine considers a read, for a host outside Bearing — the <b>allow</b>-list
    /// that <see cref="ExternalSqlPolicy"/> applies, the mirror image of <see cref="RiskyVerbs"/>.
    /// <para>
    /// Per engine and not a translation, the shape <see cref="EditExpressions"/> already has: Postgres
    /// answers <c>SHOW</c> and <c>TABLE</c>, T-SQL does not, and a keyword that means a read in one may
    /// mean something else in the other. Keep it small — a verb added here is a shape an agent may send at
    /// a production server, and the default for anything absent is refusal, which is the point.
    /// </para>
    /// </summary>
    IReadOnlySet<string> ExternalReadVerbs { get; }

    /// <summary>
    /// Whether <see cref="ExplainSql"/>'s text is this engine's. It is Postgres' — <c>EXPLAIN (FORMAT
    /// JSON)</c>, and <c>ExplainPlanParser</c> reads Postgres' plan JSON back — and nothing checked, so
    /// `bearing explain` against a SQL Server connection failed with a raw <c>Incorrect syntax near
    /// 'EXPLAIN'</c> while the help advertised the command for any exposed connection.
    /// <para>
    /// A flag rather than a second plan format: SQL Server's showplan is different XML with different
    /// measurements, and producing <see cref="Core.Explain.ExplainPlan"/> from it is a feature, not a
    /// translation (§5.4a's rule). Until then the honest answer is that this engine is not served, said
    /// before the statement is sent.
    /// </para>
    /// </summary>
    bool SupportsExplainPlan { get; }

    /// <summary>
    /// Functions refused on an exposed connection even inside an otherwise-ordinary read — the ones that
    /// reach the server's filesystem, its other sessions, or this session's own settings.
    /// <para>
    /// Accident prevention, not a boundary: see <see cref="ExternalSqlPolicy.DeniedFunction"/> for why a
    /// name scan cannot be one. Bare names, no schema and no argument list; the policy matches a call.
    /// </para>
    /// </summary>
    IReadOnlySet<string> ExternalDeniedFunctions { get; }

    /// <summary>
    /// Catalogs refused on an exposed connection because of what they hold: credentials (§1.8). The same
    /// accident-prevention posture as <see cref="ExternalDeniedFunctions"/>, aimed at a read rather than a
    /// call.
    /// <para>
    /// §1.8 already forbids Bearing's own catalog reads from selecting these, and an exposed connection
    /// reintroduced every one of them: <c>select umoptions from pg_user_mappings</c> is a plain SELECT, the
    /// allow-list admits it, and a foreign server's password comes back in cleartext — to the mapping's
    /// <i>owner</i>, not only to a superuser, so it does not even need the over-privileged connection the
    /// other two do.
    /// </para>
    /// <para>
    /// Whole relations rather than columns, because <c>select *</c> names no column and would otherwise walk
    /// straight past a column list. Bare names, no schema; the policy matches a mention.
    /// </para>
    /// </summary>
    IReadOnlySet<string> ExternalDeniedRelations { get; }

    /// <summary>
    /// The identifiers this statement calls as functions, and the ones it mentions at all — lexed, so
    /// comments, delimiters and string literals are already dealt with. Per dialect because the lexer is.
    /// </summary>
    (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned) ExternalNameScan(string statement);

    /// <summary>
    /// A statement shape refused on an exposed connection because of what it returns — credentials (§1.8) —
    /// even though its lead verb is on <see cref="ExternalReadVerbs"/>: the name of the shape, or null. Where
    /// <see cref="ExternalDeniedRelations"/> fences a catalog, this fences a command that reads one for you
    /// (MySQL's <c>SHOW CREATE USER</c> prints the password hash). Same accident-prevention posture.
    /// </summary>
    string? ExternalDeniedStatement(string statement) => null;

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

    /// <summary>
    /// Which transaction-control statement <paramref name="words"/> begins — the engine's own name for it
    /// (<c>COMMIT</c>, <c>BEGIN TRANSACTION</c>, <c>SAVEPOINT</c>, …) — or null when it is an ordinary
    /// statement. <paramref name="words"/> is the statement's on-channel tokens, as the guard already lexed
    /// them; only the leading one or two are read.
    /// <para>
    /// Per dialect because the spelling genuinely differs, and not by translation: in T-SQL a bare
    /// <c>BEGIN</c> opens a <c>BEGIN…END</c> <i>block</i> and is not transaction control at all, while in
    /// Postgres it is exactly a transaction. Getting that backwards would either refuse every T-SQL block
    /// or let a raw <c>COMMIT</c> through — and a <c>COMMIT</c> that reaches the server behind a held
    /// transaction leaves the driver's transaction object claiming something the server no longer has.
    /// </para>
    /// </summary>
    string? TransactionControl(IReadOnlyList<string> words);

    /// <summary>
    /// Whether the statement <paramref name="words"/> begins is transaction control only <em>implicitly</em>:
    /// it commits the open transaction as a side effect of doing something else (MySQL's DDL,
    /// <c>LOCK TABLES</c>, <c>ANALYZE</c>, …). Always false where <see cref="TransactionControl"/> is null, and
    /// false on engines with no such statements — Postgres and SQL Server run DDL inside the transaction.
    /// </summary>
    bool CommitsImplicitly(IReadOnlyList<string> words) => false;

    // ---- Statement splitting ----

    /// <summary>
    /// Split a buffer into top-level statement spans, in <b>this engine's</b> lexical rules — what "run
    /// the statement at the caret", the statement-highlight margin, folding and statement-scoped
    /// completion are all built on. Same reasoning as <see cref="DescribeStatements"/>: the lexing is the
    /// whole ballgame. A lexer that does not know the engine's batch separator reads a <c>GO</c>-separated
    /// batch as one statement, so "run current statement" runs the whole buffer; one that cannot see a
    /// delimited identifier splits inside <c>[a;b]</c> and offers Run half a name.
    /// <para>
    /// This is a read-side question, so a mis-split costs the wrong statement being <em>offered</em> rather
    /// than a write being mis-classified — but Run sends what is offered, so it reaches the server all the
    /// same. Spans are ordered and never overlap; they need not tile the buffer (T-SQL's leave the
    /// <c>GO</c> out, since the server cannot parse it).
    /// </para>
    /// </summary>
    IReadOnlyList<StatementSpan> SplitStatements(string sql);

    /// <summary>
    /// Whether <paramref name="fragment"/> ends in a comment that runs to end of line, so a <c>;</c> appended
    /// on the same line would be swallowed by it and the next statement would run on as part of this one —
    /// what <see cref="StatementSplitter.EnsureSeparated(ISqlDialect,string)"/> asks before terminating a part.
    /// <para>
    /// The default reads with the PostgreSQL lexer, which answers for T-SQL too: <c>--</c> and <c>'…'</c> are
    /// the same lexical forms there, and where they differ (<c>[a--b]</c>) a wrong "yes" costs a newline, not
    /// a statement. MySQL overrides it, because its <c>#</c> comment is an operator to that lexer.
    /// </para>
    /// </summary>
    bool EndsWithLineComment(string fragment) => StatementSplitter.EndsWithPostgresLineComment(fragment);

    /// <summary>
    /// Whether <paramref name="sql"/> holds a client-side directive that redefines the statement terminator
    /// (MySQL's <c>DELIMITER</c>). Such a directive is the client's, not SQL: a lone statement wrapped in one
    /// is sent as its span's text, never as typed. False on engines with no such directive.
    /// </summary>
    bool HasDelimiterDirective(string sql) => false;

    /// <summary>
    /// Whether <see cref="UpdateDeleteTarget"/>'s reduction — which reads with the PostgreSQL lexer — sees
    /// <paramref name="statement"/> the way this engine's server will: every character the server reads as
    /// code is code to that lexer and the reverse, and it is one statement. False declines the count, which
    /// is always safe (a missing number costs nothing; a number about different rows is a false
    /// reassurance, #112).
    /// <para>
    /// True by default: Postgres is the lexer's own language, and SQL Server's statements reach the reduction
    /// only after its own guard has split them. MySQL overrides it — <c>#</c> comments, executable
    /// <c>/*! … */</c> comments, backslash escapes and <c>$x$</c> names all read differently there.
    /// </para>
    /// </summary>
    /// <param name="statement">The statement as typed.</param>
    /// <param name="postgresTokens">Its on-channel tokens as the PostgreSQL lexer read them, so the check
    /// compares against the very tokens the reduction will use rather than lexing a second time.</param>
    bool ReadsLikePostgresForCounting(string statement, IReadOnlyList<Antlr4.Runtime.IToken> postgresTokens)
        => true;

    /// <summary>
    /// How a sequence function takes the sequence named by <paramref name="qualifiedName"/> (already quoted
    /// by this dialect): Postgres' <c>nextval</c> takes a <c>regclass</c> written as a string, so the default
    /// is the name in single quotes. MariaDB's takes the name itself — a string there is error 1064.
    /// </summary>
    string SequenceReference(string qualifiedName) => $"'{qualifiedName}'";

    /// <summary>
    /// The first <paramref name="rows"/> rows of <paramref name="qualifiedTable"/> (already quoted by this
    /// dialect), for the schema tree's "Select top N rows". A whole-statement hook because where the limit goes
    /// varies: Postgres and MySQL end with <c>limit N</c> (the default), T-SQL puts <c>top (N)</c> after
    /// <c>select</c>. No <c>order by</c>: which rows come first is the server's choice, as it is for any
    /// unordered query — the statement opens in an editor, where one can be added.
    /// </summary>
    string SelectTopRows(string qualifiedTable, int rows) => $"select * from {qualifiedTable} limit {rows}";

    /// <summary>
    /// Why the SQL formatter must not lay out this engine's SQL, or null when it may. The formatter reads
    /// PostgreSQL; a dialect whose lexical forms it misreads in a way that changes the statement (MySQL's
    /// <c>#</c> comment) refuses rather than be formatted into a different statement. The text completes
    /// "Not formatted — …".
    /// </summary>
    string? FormatRefusal => null;

    /// <summary>
    /// Offset just past the last token of <paramref name="sql"/> that is code — not whitespace, not a comment
    /// — or null when there is none (or this engine can't say). A split can hand a statement the comment lines
    /// that follow it (the PostgreSQL split keeps an own-line comment with the statement above when a blank
    /// line follows it), so "where does this statement end on screen" is asked here rather than read off
    /// <see cref="StatementSpan.TrimmedEnd"/>.
    /// </summary>
    int? CodeEnd(string sql) => null;

    /// <summary>
    /// Whether <paramref name="offset"/> sits inside a character literal, in this engine's lexical
    /// rules. The completion engine asks this <em>first</em> — a caret inside a string gets no
    /// suggestions — which is what made getting it wrong so total: while this was answered with the
    /// PostgreSQL lexer on every dialect, the apostrophe in a T-SQL <c>[O'Donnell]</c> opened a string
    /// that never closed, and completion returned nothing for the rest of the buffer.
    /// </summary>
    bool InStringLiteral(string sql, int offset);

    /// <summary>
    /// <paramref name="sql"/> with its literal <em>values</em> replaced by <c>?</c>, keeping the shape
    /// of the statement — what the query log stores when <c>QueryLogRedactLiterals</c> is on (#22, §1.3).
    /// <para>
    /// Per-dialect because a literal is a lexical fact: read with the wrong lexer, T-SQL's
    /// <c>0xDEADBEEF</c> came out as <c>?xDEADBEEF</c> — the <c>0</c> redacted as a number and the rest
    /// kept as an identifier — so the bytes survived in a log the setting promised to strip.
    /// </para>
    /// </summary>
    string RedactLiterals(string? sql);

    /// <summary>
    /// Every <c>:name</c> script variable in <paramref name="sql"/>, read with this engine's lexer so a
    /// colon in a string, a comment or a delimited name — or either half of <c>::</c> — is not taken for
    /// one. See <see cref="SqlVariables"/>; callers go through it rather than here.
    /// </summary>
    IReadOnlyList<SqlVariableRef> FindVariables(string sql);

    /// <summary><paramref name="value"/> as this engine's string literal — what a <c>:'name'</c>
    /// variable is replaced with. T-SQL's carries the <c>N</c> prefix so non-Latin text survives.</summary>
    string TextLiteral(string value);

    // ---- Completion ----

    /// <summary>
    /// This engine's grammar, as much of it as completion needs. One <see cref="CompletionEngine"/>
    /// serves every dialect; what changes between them is the parse, which is why this is a handle on a
    /// grammar rather than a second engine.
    /// <para>
    /// Unlike the rest of this interface it is not text-only: an <see cref="ISqlParseRules"/> hands out
    /// ANTLR lexers, parsers and token types. That stays inside <c>Bearing.Sql</c>, which is where the
    /// vendored grammars already live, so the layering is unchanged (§2.2) and nothing in
    /// <c>Core</c> or above ever sees an ANTLR type.
    /// </para>
    /// </summary>
    ISqlParseRules ParseRules { get; }
}
