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
    /// Naming them here makes manual-commit mode refuse them by name while a transaction is held, or while the
    /// batch's other writes would open one (<c>WriteRefusal.ReasonForTransactionControl</c>). Temporary tables
    /// are the documented exception and are not listed.
    /// </para>
    /// </summary>
    public string? TransactionControl(IReadOnlyList<string> words) => Control(words).Name;

    /// <inheritdoc/>
    /// <remarks>True for every statement <see cref="TransactionControl"/> names "(commits implicitly)" — the
    /// same classification, typed, so no caller has to read it back out of the label.</remarks>
    public bool CommitsImplicitly(IReadOnlyList<string> words) => Control(words).Implicit;

    private static (string? Name, bool Implicit) Control(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return (null, false);
        }
        var first = words[0].ToUpperInvariant();
        var second = words.Count > 1 ? words[1].ToUpperInvariant() : "";

        switch (first)
        {
            case "START" when second == "TRANSACTION":
                return ("START TRANSACTION", false);
            case "BEGIN" when second != "NOT":
                return ("BEGIN", false);
            case "COMMIT" or "ROLLBACK" or "SAVEPOINT" or "XA":
                return (first, false);
            case "RELEASE" when second == "SAVEPOINT":
                return ("RELEASE SAVEPOINT", false);
            // Anywhere in the SET, not just first: `set names utf8mb4, autocommit = 1` commits all the same.
            // A word called autocommit elsewhere in the statement over-refuses on manual commit only (§1.2).
            case "SET" when words.Skip(1).Any(w => w.Equals("AUTOCOMMIT", StringComparison.OrdinalIgnoreCase)):
                return ("SET autocommit", false);
            case "CREATE" or "DROP" when second == "TEMPORARY":
                return (null, false);
            // Account and replication statements that commit implicitly but whose lead word alone is also
            // the lead of statements that do not (`SET @x`, `LOAD DATA`, `START TRANSACTION`).
            case "SET" when second == "PASSWORD":
                return Implicit("SET PASSWORD");
            case "LOAD" when second == "INDEX":
                return Implicit("LOAD INDEX");
            case "START" or "STOP" when ReplicationTargets.Contains(second):
                return Implicit($"{first} {second}");
            case "CHANGE" when second is "MASTER" or "REPLICATION":
                return Implicit($"CHANGE {second}");
            // MariaDB's ANALYZE *statement* (`analyze [format=json] select …`) runs the statement and reports
            // its plan; only ANALYZE TABLE commits. Measured on 11.4: rows inserted before an `analyze select`
            // or `analyze delete` roll back, those before an `analyze table` do not.
            case "ANALYZE" when AnalyzedStatementLeads.Contains(second):
                return (null, false);
        }

        return ImplicitCommitLeads.Contains(first) ? Implicit(first) : (null, false);
    }

    /// <summary>The manual-commit refusal lists these by name, so the name says why.</summary>
    private static (string Name, bool Implicit) Implicit(string statement) => ($"{statement} ({StatementRisk.ImplicitCommitNote})", true);

    /// <summary>The word after <c>ANALYZE</c> in MariaDB's statement form — never <c>TABLE</c>,
    /// <c>NO_WRITE_TO_BINLOG</c> or <c>LOCAL</c>, which lead ANALYZE TABLE.</summary>
    private static readonly HashSet<string> AnalyzedStatementLeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "FORMAT", "SELECT", "WITH", "INSERT", "REPLACE", "UPDATE", "DELETE",
    };

    private static readonly HashSet<string> ReplicationTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "REPLICA", "SLAVE", "GROUP_REPLICATION", "ALL",
    };

    /// <summary>Leads that commit implicitly whatever follows. <c>RESET</c> covers <c>RESET MASTER</c>,
    /// <c>RESET REPLICA</c> / <c>RESET SLAVE</c> and <c>RESET QUERY CACHE</c> alike.</summary>
    private static readonly HashSet<string> ImplicitCommitLeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "RENAME", "TRUNCATE",
        "GRANT", "REVOKE",
        "LOCK", "UNLOCK",
        "ANALYZE", "CHECK", "OPTIMIZE", "REPAIR", "CACHE",
        "FLUSH", "RESET", "INSTALL", "UNINSTALL",
    };

    public IReadOnlyList<StatementSpan> SplitStatements(string sql) => StatementSplitter.SplitWithMySqlScanner(sql);

    /// <inheritdoc/>
    /// <remarks>
    /// MySQL's <c>#</c> comment is an operator to the Postgres lexer, so <c>select 1 # note</c> looked
    /// comment-free and its <c>;</c> went inside the comment — the next statement then ran on as part of this
    /// one. Any text after a MySQL statement's last token is a comment, so the <c>;</c> goes on a line of its
    /// own.
    /// </remarks>
    public bool EndsWithLineComment(string fragment)
    {
        var tokens = MySqlScanner.Tokenize(fragment);
        var codeEnd = tokens.Count == 0 ? 0 : tokens[^1].Start + tokens[^1].Length;
        return fragment.AsSpan(codeEnd).Trim().Length > 0;
    }

    /// <inheritdoc/>
    public bool HasDelimiterDirective(string sql) => MySqlScanner.HasDirective(sql);

    /// <inheritdoc/>
    public bool ReadsLikePostgresForCounting(string statement, IReadOnlyList<Antlr4.Runtime.IToken> postgresTokens)
        => UpdateDeleteTarget.ReadsAlikeOnMySql(statement, postgresTokens);

    /// <inheritdoc/>
    /// <remarks>MariaDB (the one MySQL-family engine with sequences) takes the name itself.</remarks>
    public string SequenceReference(string qualifiedName) => qualifiedName;

    /// <inheritdoc/>
    /// <remarks>To the formatter's lexer MySQL's <c>#</c> comment is an operator: it parses
    /// <c>select a, b # note⏎, c from t</c> and lays it out as <c>b # note,⏎c</c> — which MySQL runs as
    /// <c>b c</c>, column c gone and b renamed.</remarks>
    public string? FormatRefusal => "the formatter reads PostgreSQL, not MySQL / MariaDB";

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
    /// <c>mysql.slave_master_info</c> holds the replication password when the repository is a table;
    /// MySQL 8's <c>mysql.password_history</c> keeps every account's previous password hashes.
    /// Qualified, as on SQL Server: <c>user</c> and <c>servers</c> are ordinary table names, and refusing
    /// them bare would be a false statement about the user's own data. The two names nobody gives a table
    /// are matched bare as well, for a connection whose default database is <c>mysql</c>.
    /// </remarks>
    public IReadOnlySet<string> ExternalDeniedRelations { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mysql.user", "mysql.global_priv", "mysql.servers", "mysql.slave_master_info", "mysql.password_history",
        "global_priv", "slave_master_info",
    };

    /// <inheritdoc/>
    /// <remarks>
    /// <c>SHOW CREATE USER</c> prints the account's <c>IDENTIFIED … AS '&lt;hash&gt;'</c>, and MariaDB's
    /// <c>SHOW CREATE SERVER</c> a FEDERATED/CONNECT server's <c>PASSWORD</c> option — the contents of
    /// <c>mysql.user</c> / <c>mysql.servers</c>, which <see cref="ExternalDeniedRelations"/> already refuses.
    /// MariaDB's <c>SHOW GRANTS</c> carries the account's <c>IDENTIFIED BY PASSWORD '&lt;hash&gt;'</c> (§1.8),
    /// so it is refused on both engines — the allow-list is per dialect, not per server.
    /// <c>SHOW BINLOG EVENTS</c> / <c>SHOW RELAYLOG EVENTS</c> replay the logged statements, and the log
    /// carries every <c>CREATE USER</c> / <c>ALTER USER</c> with its <c>IDENTIFIED … AS '&lt;hash&gt;'</c>.
    /// Read off the scanner's bare words, so a comment between the words, or an executable comment around
    /// them, does not hide the shape.
    /// </remarks>
    public string? ExternalDeniedStatement(string statement)
    {
        foreach (var s in MySqlScanner.Split(statement))
        {
            var words = MySqlScanner.TopLevelWords(s.Tokens);
            if (words.Count >= 3 && words[0] == "SHOW" && words[1] == "CREATE" && words[2] is "USER" or "SERVER")
            {
                return $"SHOW CREATE {words[2]}";
            }
            if (words.Count >= 2 && words[0] == "SHOW" && words[1] == "GRANTS")
            {
                return "SHOW GRANTS";
            }
            if (words.Count >= 3 && words[0] == "SHOW" && words[1] is "BINLOG" or "RELAYLOG" && words[2] == "EVENTS")
            {
                return $"SHOW {words[1]} EVENTS";
            }
        }
        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Under <c>ANSI_QUOTES</c> a double-quoted run is a <em>name</em> — <c>select * from "mysql"."user"</c>
    /// reads the password hashes — and whether the server is in that mode is not something this layer knows
    /// (the write guard's dual reading has the same cause). So a statement holding <c>"</c> is also read with
    /// its double quotes as backticks, and the two scans are united: widening only.
    /// <para>
    /// <c>NO_BACKSLASH_ESCAPES</c> is the same kind of unknown: <c>select 'a\' , load_file('/etc/passwd') #'</c>
    /// is one string by default and a call under that mode, with no delimiter for the guard's second reading
    /// to split at. A backslash only ever changes where a literal ends, so a statement holding one is also
    /// read with every backslash blanked (the same token boundaries that mode gives), with and without the
    /// <c>ANSI_QUOTES</c> rewrite.
    /// </para>
    /// </remarks>
    public (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned) ExternalNameScan(string statement)
    {
        var revealed = MySqlScanner.RevealExecutableComments(statement);
        var tokens = MySqlParsing.LexAll(revealed);
        var called = SqlNameScan.CalledNames(tokens);
        var mentioned = SqlNameScan.MentionedNames(tokens);

        var readings = new List<string>();
        if (revealed.Contains('"'))
        {
            readings.Add(revealed.Replace('"', '`'));
        }
        if (statement.Contains('\\'))
        {
            // Revealed after the blanking, not before: which `/*!` opens an executable comment depends on where
            // the literals end, so `select 'a\' /*!, load_file('/etc/passwd') */ #'` hides the call inside a
            // string by default and runs it under NO_BACKSLASH_ESCAPES.
            var unescaped = MySqlScanner.RevealExecutableComments(statement.Replace('\\', ' '));
            readings.Add(unescaped);
            if (unescaped.Contains('"'))
            {
                readings.Add(unescaped.Replace('"', '`'));
            }
        }
        foreach (var reading in readings)
        {
            var other = MySqlParsing.LexAll(reading);
            called.UnionWith(SqlNameScan.CalledNames(other));
            mentioned.UnionWith(SqlNameScan.MentionedNames(other));
        }
        return (called, mentioned);
    }
}
