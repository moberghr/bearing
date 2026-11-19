namespace Bearing.Sql;

/// <summary>
/// The Amazon DynamoDB dialect: PartiQL, read with the vendored PostgreSQL lexer.
/// <para>
/// <b>Why that lexer.</b> PartiQL shares PostgreSQL's lexical forms where they matter to a guard and a
/// splitter: <c>'…'</c> strings with <c>''</c>, <c>"…"</c> names with <c>""</c>, <c>--</c> and <c>/* */</c>
/// comments, and <c>;</c> between statements. Its own punctuation — <c>[ ]</c> lists, <c>{ }</c> tuples,
/// <c>&lt;&lt; &gt;&gt;</c> bags and <c>?</c> parameters — reads as brackets, operators or stray characters,
/// none of which can open a string or a comment, so a statement holding them still splits and classifies
/// (pinned by the corpus in <c>PartiQlDialectTests</c>). Where the lexer does read text differently — a
/// Postgres-only form such as a dollar-quoted body or a nested comment — the batch is declared unreadable
/// rather than trusted (<see cref="PartiQlScan.ReadsAsPartiQl"/>).
/// </para>
/// <para>
/// <b>Most of the interface answers "not on this engine".</b> DynamoDB has no <c>LIMIT</c>/<c>OFFSET</c>
/// (results continue from a token the executor owns), no derived tables, no transactions Bearing holds, no
/// query plan Bearing reads, no script variables and no grid editing in v1. Each of those members answers null,
/// empty or false — never an approximation in another engine's syntax (§1.1).
/// </para>
/// </summary>
public sealed class PartiQlDialect : ISqlDialect
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static PartiQlDialect Instance { get; } = new();

    /// <summary>Matches <c>DynamoDbProvider.ProviderId</c>, which lives in <c>Bearing.Data</c> and so cannot be
    /// referenced from here (§2.2).</summary>
    public string Id => "dynamodb";

    // ---- Identifiers ----

    /// <summary>A double-quoted name with <c>""</c> for a quote inside — PartiQL's form, and Postgres'.</summary>
    public string Quote(string identifier) => PgIdentifier.Quote(identifier);

    /// <summary>Always quoted: see <see cref="NeedsQuoting"/>.</summary>
    public string QuoteIfNeeded(string identifier) => Quote(identifier);

    /// <summary>
    /// Always true. DynamoDB reserves several hundred words in PartiQL and this dialect does not carry that
    /// list, so it cannot say a bare name is safe; a quoted name always means itself, and table names with
    /// <c>-</c> or <c>.</c> (both legal in DynamoDB) need quoting anyway.
    /// </summary>
    public bool NeedsQuoting(string identifier) => true;

    public string Unquote(string identifier) => PgIdentifier.Unquote(identifier);

    // ---- Paging and counting ----

    /// <summary>Null: PartiQL has no <c>LIMIT</c> or <c>OFFSET</c>. A DynamoDB result continues from the
    /// executor's continuation token instead, so a null here retires offset paging for the result.</summary>
    public string? TryAppendPage(string sql, int offset, int limit) => null;

    /// <summary>Null: PartiQL on DynamoDB has no derived tables to page from the outside.</summary>
    public string? Wrap(string sql, int offset, int limit) => null;

    /// <summary>Null: there is no <c>count(*)</c> over a query here, so the total is reported unavailable.</summary>
    public string? CountWrap(string sql) => null;

    /// <summary>
    /// The first rows of a table: <c>select * from "T"</c>, with no row count — PartiQL has no <c>LIMIT</c>, and
    /// the result is read a page at a time anyway. Whether it asks before scanning is the run path's decision.
    /// </summary>
    public string SelectTopRows(string qualifiedTable, int rows) => $"select * from {qualifiedTable}";

    // ---- DML ----

    /// <summary>Grid editing is not offered on DynamoDB (the grid is locked first), and PartiQL's
    /// <c>INSERT … VALUE {…}</c> cannot be built from a column list — so there is no honest text to return.</summary>
    public string InsertStatement(string qualifiedTable, string columnList, string valueList, bool withReturning)
        => throw new NotSupportedException(GridEditingWithheld);

    /// <inheritdoc cref="InsertStatement"/>
    public string UpdateStatement(
        string qualifiedTable, string setList, string whereClause, IReadOnlyList<string>? returningColumns)
        => throw new NotSupportedException(GridEditingWithheld);

    /// <summary>Null: with no grid editing there is no cell to put an expression in.</summary>
    public string? TryEditExpression(string? text) => null;

    /// <inheritdoc cref="ISqlDialect.EditExpressions"/>
    public IReadOnlyCollection<string> EditExpressions => [];

    /// <inheritdoc cref="ISqlDialect.OfferedEditExpressions"/>
    public IReadOnlyList<string> OfferedEditExpressions => [];

    // ---- Write guard ----

    /// <summary>
    /// True: the guard reads PartiQL with the PostgreSQL lexer, which shares its lexical forms (see the type
    /// summary), and anything it is unsure of is risky by construction (<see cref="DescribeStatements"/>). A
    /// batch the lexer would read differently from DynamoDB is still reported as unreadable, statement by
    /// statement, so this flag never vouches for one.
    /// </summary>
    public bool HasDialectAwareGuard => true;

    /// <summary>PartiQL's three write verbs. Every other lead word except <c>SELECT</c> is risky too, through
    /// the conservative default in <see cref="DescribeStatements"/>, so this set is never narrower than another
    /// engine's in effect (§1.2).</summary>
    public IReadOnlySet<string> RiskyVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE",
    };

    /// <summary><c>SELECT</c> alone: it is PartiQL's only read.</summary>
    public IReadOnlySet<string> ExternalReadVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT",
    };

    /// <inheritdoc/>
    /// <remarks>DynamoDB has no plan Bearing can read.</remarks>
    public bool SupportsExplainPlan => false;

    /// <summary>Empty: PartiQL on DynamoDB has no function that reaches past the data — no filesystem, no other
    /// sessions, no session settings.</summary>
    public IReadOnlySet<string> ExternalDeniedFunctions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Empty: DynamoDB has no catalog tables a statement can read, credential-bearing or otherwise.</summary>
    public IReadOnlySet<string> ExternalDeniedRelations { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned) ExternalNameScan(string statement)
        => PostgresDialect.Instance.ExternalNameScan(statement);

    /// <summary>
    /// The PostgreSQL scan, then two conservative rules of this engine's own:
    /// <list type="bullet">
    ///   <item>a statement whose lead word is not <c>SELECT</c> and that named no write is risky anyway, marked
    ///     <see cref="StatementRisk.FromConservativeDefault"/> — PartiQL's only read is <c>SELECT</c>, so
    ///     anything else is confirmed rather than waved past (T-SQL's arrangement);</item>
    ///   <item>when the lexer would not read the batch as DynamoDB does (<see cref="PartiQlScan.ReadsAsPartiQl"/>),
    ///     every statement is reported unreadable (<see cref="StatementRisk.GuardIsDialectAware"/> false) — and a
    ///     batch the lexer saw no statement in at all still yields one, because DynamoDB may see one there.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<StatementRisk> DescribeStatements(string sql)
    {
        var described = WriteGuard.DescribeWithPostgresLexer(sql, this);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return described;
        }

        if (!PartiQlScan.ReadsAsPartiQl(sql))
        {
            return described.Count == 0
                ? [new StatementRisk(sql.Trim(), UnreadableVerb, [], GuardIsDialectAware: false)]
                : described.Select(s => s with { GuardIsDialectAware = false }).ToList();
        }

        return described
            .Select(s => s.RiskyVerbs.Count == 0 && !s.Verb.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                ? s with { RiskyVerbs = [s.Verb], FromConservativeDefault = true }
                : s)
            .ToList();
    }

    /// <summary>Always null: Bearing holds no transaction on DynamoDB (no manual commit), and PartiQL's
    /// transactions are an API call, not a statement.</summary>
    public string? TransactionControl(IReadOnlyList<string> words) => null;

    /// <summary>False: nothing commits implicitly where no transaction is held.</summary>
    public bool CommitsImplicitly(IReadOnlyList<string> words) => false;

    /// <summary>
    /// False: decline the row-impact count. Its reduction would count with a <c>select * from</c> wrapped in a
    /// derived table, which DynamoDB cannot run — and a declined count is always safe (#112), where a count over
    /// a full table would be a scan nobody asked for.
    /// </summary>
    public bool ReadsLikePostgresForCounting(string statement, IReadOnlyList<Antlr4.Runtime.IToken> postgresTokens)
        => false;

    // ---- Statement splitting ----

    /// <summary>The PostgreSQL split: <c>;</c> outside strings, quoted names and comments (and the editor's
    /// blank-line convention, which <c>EnsureSeparated</c> turns into <c>;</c> before a run).</summary>
    public IReadOnlyList<StatementSpan> SplitStatements(string sql) => StatementSplitter.SplitWithPostgresLexer(sql);

    /// <inheritdoc/>
    public int? CodeEnd(string sql) => PostgresDialect.Instance.CodeEnd(sql);

    /// <inheritdoc/>
    public string? FormatRefusal => "the formatter reads PostgreSQL, not PartiQL (Amazon DynamoDB)";

    /// <inheritdoc/>
    public IReadOnlyList<ScanTarget>? ScanTargets(string sql) => PartiQlScan.Targets(sql);

    /// <summary>The PostgreSQL lexer's reading, which PartiQL's <c>'…'</c> with <c>''</c> shares.</summary>
    public bool InStringLiteral(string sql, int offset) => SqlStringLiterals.Contains(sql, offset);

    /// <summary>The same lexer's redaction: strings and numbers are the literals PartiQL has.</summary>
    public string RedactLiterals(string? sql) => SqlRedactor.Redact(sql);

    /// <summary>Empty: script variables are not offered on this engine; the text is sent as typed.</summary>
    public IReadOnlyList<SqlVariableRef> FindVariables(string sql) => [];

    /// <summary>Empty: <c>@set</c> lines are not recognised on this engine; the text is sent as typed.</summary>
    public IReadOnlyList<ScriptDirective> FindDirectives(string sql) => [];

    // ---- Completion ----

    /// <summary>The PostgreSQL grammar: table names and declared key attributes complete; keyword completion
    /// is Postgres' (a known limitation — there is no PartiQL grammar here).</summary>
    public ISqlParseRules ParseRules => PgParseRules.Instance;

    /// <summary>What a statement the guard could find nothing in is labelled as, when DynamoDB might.</summary>
    private const string UnreadableVerb = "STATEMENT";

    private const string GridEditingWithheld = "Grid editing isn't available on Amazon DynamoDB connections.";
}
