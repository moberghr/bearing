using System.Text.RegularExpressions;

namespace Bearing.Sql;

/// <summary>
/// MySQL / MariaDB identifier quoting — backticks, and the third answer to the question
/// <see cref="PgIdentifier"/> and <see cref="SqlServerIdentifier"/> answer for their engines.
/// <para>
/// Like SQL Server and unlike Postgres, MySQL does not fold an unquoted name: <c>Customers</c> means
/// <c>Customers</c> (whether a <em>table</em> name is then matched case-sensitively is the server's
/// <c>lower_case_table_names</c>, which is a file-system question, not a quoting one). So case is never a
/// reason to quote here — only a character the bare form cannot carry, or a reserved word.
/// </para>
/// <para>
/// Double quotes are deliberately <b>not</b> an identifier delimiter. Under the default <c>sql_mode</c>
/// they delimit a string, and only <c>ANSI_QUOTES</c> changes that — a per-server setting no text
/// generator can see. Backticks mean an identifier under every mode.
/// </para>
/// </summary>
public static partial class MySqlIdentifier
{
    /// <summary>
    /// Reserved words of MySQL 8.x and MariaDB 10.x/11.x, unioned. A name in this set cannot stand bare.
    /// Over-quoting costs a pair of backticks; under-quoting costs a syntax error on whichever of the two
    /// engines reserves the word, so the union is the right side to err on.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessible", "add", "all", "alter", "analyze", "and", "as", "asc", "asensitive",
        "before", "between", "bigint", "binary", "blob", "both", "by",
        "call", "cascade", "case", "change", "char", "character", "check", "collate", "column",
        "condition", "constraint", "continue", "convert", "create", "cross", "cube", "cume_dist",
        "current_date", "current_role", "current_time", "current_timestamp", "current_user", "cursor",
        "database", "databases", "day_hour", "day_microsecond", "day_minute", "day_second",
        "dec", "decimal", "declare", "default", "delayed", "delete", "delete_domain_id", "dense_rank",
        "desc", "describe", "deterministic", "distinct", "distinctrow", "div", "do_domain_ids", "double",
        "drop", "dual",
        "each", "else", "elseif", "empty", "enclosed", "escaped", "except", "exists", "exit", "explain",
        "false", "fetch", "first_value", "float", "float4", "float8", "for", "force", "foreign", "from",
        "fulltext", "function",
        "general", "generated", "get", "grant", "group", "grouping", "groups",
        "having", "high_priority", "hour_microsecond", "hour_minute", "hour_second",
        "if", "ignore", "ignore_domain_ids", "ignore_server_ids", "in", "index", "infile", "inner", "inout",
        "insensitive", "insert", "int", "int1", "int2", "int3", "int4", "int8", "integer", "intersect",
        "interval", "into", "io_after_gtids", "io_before_gtids", "is", "iterate",
        "join", "json_table",
        "key", "keys", "kill",
        "lag", "last_value", "lateral", "lead", "leading", "leave", "left", "like", "limit", "linear",
        "lines", "load", "localtime", "localtimestamp", "lock", "long", "longblob", "longtext", "loop",
        "low_priority",
        "master_bind", "master_heartbeat_period", "master_ssl_verify_server_cert", "match", "maxvalue",
        "mediumblob", "mediumint", "mediumtext", "middleint", "minute_microsecond", "minute_second", "mod",
        "modifies",
        "natural", "not", "no_write_to_binlog", "nth_value", "ntile", "null", "numeric",
        "of", "offset", "on", "optimize", "optimizer_costs", "option", "optionally", "or", "order", "out",
        "outer", "outfile", "over",
        "page_checksum", "parse_vcol_expr", "partition", "percent_rank", "position", "precision",
        "primary", "procedure", "purge",
        "range", "rank", "read", "reads", "read_write", "real", "recursive", "ref_system_id", "references",
        "regexp", "release", "rename", "repeat", "replace", "require", "resignal", "restrict", "return",
        "returning", "revoke", "right", "rlike", "row", "row_number", "rows",
        "schema", "schemas", "second_microsecond", "select", "sensitive", "separator", "set", "show",
        "signal", "slow", "smallint", "spatial", "specific", "sql", "sql_big_result", "sql_calc_found_rows",
        "sql_small_result", "sqlexception", "sqlstate", "sqlwarning", "ssl", "starting", "stats_auto_recalc",
        "stats_persistent", "stats_sample_pages", "stored", "straight_join", "system",
        "table", "terminated", "then", "tinyblob", "tinyint", "tinytext", "to", "trailing", "trigger", "true",
        "undo", "union", "unique", "unlock", "unsigned", "update", "usage", "use", "using", "utc_date",
        "utc_time", "utc_timestamp",
        "values", "varbinary", "varchar", "varcharacter", "varying", "virtual",
        "when", "where", "while", "window", "with", "write",
        "xor",
        "year_month",
        "zerofill",
    };

    /// <summary>True when <paramref name="identifier"/> must be backticked to mean itself: empty, not a
    /// plain bare name, all digits (which reads as a number), or a reserved word. Case is not a reason.</summary>
    public static bool NeedsQuoting(string identifier)
        => identifier.Length == 0
           || !BareIdentifierRegex().IsMatch(identifier)
           || Reserved.Contains(identifier);

    /// <summary>Unconditionally backtick, doubling an embedded backtick — the only escape MySQL defines
    /// inside a quoted identifier.</summary>
    public static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    /// <summary>Backtick only when <see cref="NeedsQuoting"/> says the bare form would not round-trip.</summary>
    public static string QuoteIfNeeded(string identifier)
        => NeedsQuoting(identifier) ? Quote(identifier) : identifier;

    /// <summary>Strip one layer of backticks and undouble. A double-quoted token is left alone: under the
    /// default <c>sql_mode</c> it is a string, not a name (see the type's remarks).</summary>
    public static string Unquote(string identifier)
    {
        if (identifier.Length >= 2 && identifier[0] == '`' && identifier[^1] == '`')
        {
            return identifier[1..^1].Replace("``", "`");
        }
        return identifier;
    }

    /// <summary>
    /// A name that can stand bare: letters, digits, <c>_</c> and <c>$</c>, plus anything outside ASCII
    /// (MySQL admits U+0080 upward). The leading character must not be a digit — MySQL allows one, but
    /// <c>1e5</c> and <c>0x1F</c> are then numbers, and a name that only sometimes means itself is one to
    /// quote.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z_$\u0080-￿][A-Za-z0-9_$\u0080-￿]*$")]
    private static partial Regex BareIdentifierRegex();
}
