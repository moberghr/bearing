using System.Linq;

namespace Bearing.Sql;

/// <summary>
/// The MySQL / MariaDB dialect: one dialect for both engines, because the text this interface generates —
/// backtick quoting, <c>LIMIT … OFFSET …</c> paging, the derived-table wrap, the write guard — is the same on
/// both. Where the two genuinely differ (MariaDB's <c>INSERT … RETURNING</c>, its <c>max_statement_time</c>
/// against MySQL's <c>max_execution_time</c>), the difference is a fact about the <em>server</em>, so the
/// provider settles it from the version it connected to rather than this text-only layer guessing (§2.2).
/// <para>
/// Everything lexical goes through <see cref="MySqlScanner"/>, never the PostgreSQL grammar, for the reason
/// <see cref="SqlServerDialect"/> gives: a lexer that cannot read the dialect answers positive questions
/// wrongly. Here the cases are the backslash escape (<c>'it\'s'</c>), backticked names, <c>#</c> comments,
/// executable <c>/*! */</c> comments and the <c>DELIMITER</c> directive — each of which the PG lexer
/// misreads in a way that reaches the server.
/// </para>
/// </summary>
public sealed class MySqlDialect : ISqlDialect
{
    public static MySqlDialect Instance { get; } = new();

    /// <summary>Matches <c>MySqlProvider.ProviderId</c>, which lives in <c>Bearing.Data</c> (§2.2).</summary>
    public string Id => "mysql";

    public string Quote(string identifier) => MySqlIdentifier.Quote(identifier);
    public string QuoteIfNeeded(string identifier) => MySqlIdentifier.QuoteIfNeeded(identifier);
    public bool NeedsQuoting(string identifier) => MySqlIdentifier.NeedsQuoting(identifier);
    public string Unquote(string identifier) => MySqlIdentifier.Unquote(identifier);

    private static readonly HashSet<string> RowReturningStarts =
        new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH" };

    /// <summary>
    /// A top-level occurrence of any of these means a page clause must <b>not</b> be appended:
    /// <c>LIMIT</c> — the query caps itself (MariaDB's <c>LIMIT ROWS EXAMINED</c> included);
    /// <c>OFFSET</c>/<c>FETCH</c> — MariaDB 10.6's standard paging; <c>FOR</c> and <c>LOCK</c> — the
    /// locking clauses (<c>FOR UPDATE</c>, <c>FOR SHARE</c>, <c>LOCK IN SHARE MODE</c>), which must follow
    /// <c>LIMIT</c>; <c>INTO</c> — <c>SELECT … INTO</c> a variable or a file, which a limit would not make a
    /// read; <c>PROCEDURE</c> — the legacy <c>PROCEDURE ANALYSE()</c>, which is last.
    /// </summary>
    private static readonly HashSet<string> BlockingKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIMIT", "OFFSET", "FETCH", "FOR", "LOCK", "INTO", "PROCEDURE",
    };

    /// <summary>
    /// <c>limit n</c> — or <c>limit n offset m</c> past the first page — on its own line, so a trailing
    /// <c>-- </c> or <c>#</c> comment cannot swallow it. Both engines accept the clause with or without an
    /// <c>ORDER BY</c>, so unlike T-SQL there is no second form; an order is never synthesised.
    /// </summary>
    public string? TryAppendPage(string sql, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(sql) || limit <= 0 || offset < 0)
        {
            return null;
        }

        var statements = MySqlScanner.Split(sql);
        if (statements.Count != 1)
        {
            return null;
        }
        if (WriteGuard.HasRisk(this, sql))
        {
            return null;
        }

        var words = MySqlScanner.TopLevelWords(statements[0].Tokens);
        if (words.Count == 0 || !RowReturningStarts.Contains(words[0]) || words.Any(BlockingKeywords.Contains))
        {
            return null;
        }

        var clause = offset > 0 ? $"limit {limit} offset {offset}" : $"limit {limit}";
        return $"{statements[0].Text}\n{clause}";
    }

    /// <summary>
    /// Fallback paging, from outside a derived table. Both engines admit a CTE and a locking clause inside
    /// one, but not every statement can be one: a <c>SHOW</c> or a <c>CALL</c> is not a query expression, and
    /// <c>INTO</c> is illegal there — so those are refused and the caller retires paging rather than sending
    /// SQL the server rejects.
    /// <para>
    /// One rule Postgres does not have: a derived table may not carry two columns of the same name
    /// (<c>select * from (select 1 a, 2 a) _sq</c> is error 1060). That is a property of the result, not
    /// the text, so it is the executor's to report as an uncountable shape rather than this method's to
    /// predict.
    /// </para>
    /// </summary>
    public string? Wrap(string sql, int offset, int limit)
        => DerivedTableText(sql) is { } text
            ? $"select * from (\n{text}\n) as _sq limit {limit} offset {offset}"
            : null;

    /// <summary>The total-row wrapper, refused for the same shapes as <see cref="Wrap"/>.</summary>
    public string? CountWrap(string sql)
        => DerivedTableText(sql) is { } text ? $"select count(*) from (\n{text}\n) as _sq" : null;

    private static readonly HashSet<string> DerivedTableStarts =
        new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH", "TABLE", "VALUES" };

    /// <summary>The single statement's own text — trimmed, without its delimiter, and without a leading
    /// <c>DELIMITER</c> line — when it can sit in a derived table; null when it cannot.</summary>
    private static string? DerivedTableText(string sql)
    {
        var statements = MySqlScanner.Split(sql);
        if (statements.Count != 1)
        {
            return null;
        }

        var tokens = statements[0].Tokens;
        if (tokens.Count == 0)
        {
            return null;
        }

        // A parenthesised query, `(select …) union (select …)`, is a query expression too.
        var leadsWithParen = tokens[0].Kind == MySqlTokenKind.Punctuation && tokens[0].Text == "(";
        var words = MySqlScanner.TopLevelWords(tokens);
        if (!leadsWithParen && (words.Count == 0 || !DerivedTableStarts.Contains(words[0])))
        {
            return null;
        }
        return words.Contains("INTO") ? null : statements[0].Text;
    }

    /// <summary>
    /// <c>returning *</c> — MariaDB's (10.5+). MySQL has no such clause and rejects it as a syntax error,
    /// which is why the generator always asks for both forms: the executor knows which server it is on and
    /// sends the plain form to MySQL, the same fallback SQL Server's trigger restriction already uses.
    /// </summary>
    public string InsertStatement(string qualifiedTable, string columnList, string valueList, bool withReturning)
        => $"insert into {qualifiedTable} ({columnList}) values ({valueList})"
           + (withReturning ? " returning *" : "");

    /// <summary>
    /// A plain UPDATE, always. Neither engine can read back an updated row in the same statement — MariaDB's
    /// <c>RETURNING</c> covers INSERT, REPLACE and DELETE, not UPDATE — so the grid keeps its local values for
    /// the edited cells (§5.4b's rule for anything not returned).
    /// </summary>
    public string UpdateStatement(
        string qualifiedTable, string setList, string whereClause, IReadOnlyList<string>? returningColumns)
        => $"update {qualifiedTable} set {setList} where {whereClause}";

    public string? TryEditExpression(string? text) => MySqlEditExpression.TryRecognize(text);

    /// <inheritdoc cref="ISqlDialect.EditExpressions"/>
    public IReadOnlyCollection<string> EditExpressions => MySqlEditExpression.All;

    /// <inheritdoc cref="ISqlDialect.OfferedEditExpressions"/>
    public IReadOnlyList<string> OfferedEditExpressions => MySqlEditExpression.Offered;

    /// <summary>True: <see cref="MySqlWriteGuard"/> splits and classifies with this engine's own lexical
    /// rules. A §1.2 decision, not a convenience — see <see cref="SqlServerDialect.HasDialectAwareGuard"/>.</summary>
    public bool HasDialectAwareGuard => true;

    /// <summary>
    /// The Postgres set plus MySQL's own write verbs — a superset, never a subset (§1.2). <c>REPLACE</c> and
    /// <c>LOAD DATA</c> write rows; <c>RENAME</c>, <c>IMPORT TABLE</c>, <c>INSTALL</c>/<c>UNINSTALL</c> change
    /// the schema or the server; <c>FLUSH</c>, <c>RESET</c>, <c>PURGE</c>, <c>KILL</c>, <c>SHUTDOWN</c> and
    /// <c>RESTART</c> act on the server; <c>OPTIMIZE</c>/<c>REPAIR</c> rebuild tables; <c>BINLOG</c> replays
    /// events; <c>CHANGE</c> repoints replication; <c>EXECUTE</c> runs a prepared statement, which may be
    /// anything.
    /// </summary>
    public IReadOnlySet<string> RiskyVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "COPY",
        "CREATE", "DROP", "TRUNCATE", "ALTER", "REFRESH",
        "GRANT", "REVOKE",
        "CALL", "DO",
        "REPLACE", "LOAD", "RENAME", "IMPORT", "INSTALL", "UNINSTALL",
        "FLUSH", "RESET", "PURGE", "KILL", "SHUTDOWN", "RESTART",
        "OPTIMIZE", "REPAIR", "BINLOG", "CHANGE", "EXECUTE",
    };

    public bool InStringLiteral(string sql, int offset) => MySqlLiterals.Contains(sql, offset);

    public string RedactLiterals(string? sql) => MySqlLiterals.RedactLiterals(sql);

    public IReadOnlyList<StatementRisk> DescribeStatements(string sql) => MySqlWriteGuard.Describe(sql, this);

    /// <summary>
    /// MySQL's transaction vocabulary — and, unlike the other two engines, the statements that end a
    /// transaction <em>implicitly</em>.
    /// <para>
    /// The explicit ones: <c>START TRANSACTION</c>, <c>BEGIN</c> / <c>BEGIN WORK</c> (but not MariaDB's
    /// <c>BEGIN NOT ATOMIC</c>, which opens a compound statement), <c>COMMIT</c>, <c>ROLLBACK</c>,
    /// <c>SAVEPOINT</c>, <c>RELEASE SAVEPOINT</c>, <c>XA …</c>, and <c>SET autocommit</c> (setting it to 1
    /// commits whatever is open).
    /// </para>
    /// <para>
    /// The implicit ones are why this is longer than Postgres': in MySQL and MariaDB, DDL, account
    /// statements, <c>LOCK TABLES</c> and a handful of administrative statements <b>commit the open
    /// transaction</b> before they run. On a manual-commit connection that is exactly the "transaction ended
    /// behind the driver's back" case the refusal exists for (#131): the chip would keep counting into a
    /// transaction the server already committed, and Rollback would roll back nothing while saying it had.
    /// Naming them here makes manual-commit mode refuse them by name instead. Temporary tables are the
    /// documented exception and are not listed.
    /// </para>
    /// </summary>
    public string? TransactionControl(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return null;
        }
        var first = words[0].ToUpperInvariant();
        var second = words.Count > 1 ? words[1].ToUpperInvariant() : "";

        switch (first)
        {
            case "START" when second == "TRANSACTION":
                return "START TRANSACTION";
            case "BEGIN" when second != "NOT":
                return "BEGIN";
            case "COMMIT" or "ROLLBACK" or "SAVEPOINT" or "XA":
                return first;
            case "RELEASE" when second == "SAVEPOINT":
                return "RELEASE SAVEPOINT";
            case "SET" when words.Skip(1).Take(2).Any(w => w.Equals("AUTOCOMMIT", StringComparison.OrdinalIgnoreCase)):
                return "SET autocommit";
            case "CREATE" or "DROP" when second == "TEMPORARY":
                return null;
        }

        return ImplicitCommitLeads.Contains(first) ? $"{first} (commits implicitly)" : null;
    }

    private static readonly HashSet<string> ImplicitCommitLeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "RENAME", "TRUNCATE",
        "GRANT", "REVOKE",
        "LOCK", "UNLOCK",
        "ANALYZE", "CHECK", "OPTIMIZE", "REPAIR", "CACHE",
        "FLUSH", "RESET", "INSTALL", "UNINSTALL",
    };

    public IReadOnlyList<StatementSpan> SplitStatements(string sql) => StatementSplitter.SplitWithMySqlScanner(sql);

    public ISqlParseRules ParseRules => MySqlParseRules.Instance;

    /// <summary>The reads a host outside Bearing may send. <c>SHOW</c> and the <c>DESCRIBE</c>/<c>EXPLAIN</c>
    /// family are how anyone explores a MySQL server, and <c>TABLE t</c> is MySQL 8's shorthand.
    /// <c>USE</c>, <c>HELP</c> and a standalone <c>VALUES</c> are left out — the allow-list stays small.</summary>
    public IReadOnlySet<string> ExternalReadVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "TABLE",
    };

    /// <inheritdoc/>
    /// <remarks>Postgres' plan JSON is the only plan <c>ExplainPlanParser</c> reads; MySQL's
    /// <c>EXPLAIN FORMAT=JSON</c> is a different document, so serving it would be a second reader.</remarks>
    public bool SupportsExplainPlan => false;

    /// <summary>
    /// <c>load_file()</c> reads any file the server process can — a plain SELECT, unaffected by a read-only
    /// session. <c>sys_exec</c>/<c>sys_eval</c> are the shell UDFs of <c>lib_mysqludf_sys</c>, absent from a
    /// stock server and catastrophic where present.
    /// </summary>
    public IReadOnlySet<string> ExternalDeniedFunctions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "load_file", "sys_exec", "sys_eval",
    };

    /// <inheritdoc/>
    /// <remarks>
    /// §1.8's catalogs in this engine's vocabulary. <c>mysql.user</c> and MariaDB's <c>mysql.global_priv</c>
    /// hold password hashes; <c>mysql.servers</c> holds FEDERATED/CONNECT passwords;
    /// <c>mysql.slave_master_info</c> holds the replication password when the repository is a table.
    /// Qualified, as on SQL Server: <c>user</c> and <c>servers</c> are ordinary table names, and refusing
    /// them bare would be a false statement about the user's own data. The two names nobody gives a table
    /// are matched bare as well, for a connection whose default database is <c>mysql</c>.
    /// </remarks>
    public IReadOnlySet<string> ExternalDeniedRelations { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mysql.user", "mysql.global_priv", "mysql.servers", "mysql.slave_master_info",
        "global_priv", "slave_master_info",
    };

    /// <inheritdoc/>
    public (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned) ExternalNameScan(string statement)
    {
        var tokens = MySqlParsing.LexAll(MySqlScanner.RevealExecutableComments(statement));
        return (SqlNameScan.CalledNames(tokens), SqlNameScan.MentionedNames(tokens));
    }
}
