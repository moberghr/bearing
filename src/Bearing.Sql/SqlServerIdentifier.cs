using System.Text.RegularExpressions;

namespace Bearing.Sql;

/// <summary>
/// Transact-SQL identifier quoting — the SQL Server answer to the question <see cref="PgIdentifier"/>
/// answers for Postgres, and deliberately <em>not</em> the same answer.
/// <para>
/// Postgres folds every unquoted identifier to lower case, so <c>PgIdentifier</c> demands quotes for
/// anything that is not already lower case. SQL Server preserves case: <c>Customers</c> is
/// <c>Customers</c>. Reusing the Postgres rule here would bracket every PascalCase name in the catalog
/// and turn completion output into <c>select * from [dbo].[Customers] c</c> — technically correct,
/// unreadable, and unlike anything the user types.
/// </para>
/// </summary>
public static partial class SqlServerIdentifier
{
    /// <summary>
    /// The T-SQL reserved keywords, which cannot appear as a bare identifier. Transcribed from the
    /// SQL Server documentation's "Reserved Keywords (Transact-SQL)" list (the engine's own reserved
    /// set, not the ODBC or future-reserved lists — those are advisory and would over-quote).
    /// The one documented entry that is two words, <c>WITHIN GROUP</c>, is omitted: no single
    /// identifier can collide with it.
    /// </summary>
    private static readonly HashSet<string> MustQuote = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "all", "alter", "and", "any", "as",
        "asc", "authorization", "backup", "begin", "between", "break",
        "browse", "bulk", "by", "cascade", "case", "check",
        "checkpoint", "close", "clustered", "coalesce", "collate", "column",
        "commit", "compute", "constraint", "contains", "containstable", "continue",
        "convert", "create", "cross", "current", "current_date", "current_time",
        "current_timestamp", "current_user", "cursor", "database", "dbcc", "deallocate",
        "declare", "default", "delete", "deny", "desc", "disk",
        "distinct", "distributed", "double", "drop", "dump", "else",
        "end", "errlvl", "escape", "except", "exec", "execute",
        "exists", "exit", "external", "fetch", "file", "fillfactor",
        "for", "foreign", "freetext", "freetexttable", "from", "full",
        "function", "goto", "grant", "group", "having", "holdlock",
        "identity", "identity_insert", "identitycol", "if", "in", "index",
        "inner", "insert", "intersect", "into", "is", "join",
        "key", "kill", "left", "like", "lineno", "load",
        "merge", "national", "nocheck", "nonclustered", "not", "null",
        "nullif", "of", "off", "offsets", "on", "open",
        "opendatasource", "openquery", "openrowset", "openxml", "option", "or",
        "order", "outer", "over", "percent", "pivot", "plan",
        "precision", "primary", "print", "proc", "procedure", "public",
        "raiserror", "read", "readtext", "reconfigure", "references", "replication",
        "restore", "restrict", "return", "revert", "revoke", "right",
        "rollback", "rowcount", "rowguidcol", "rule", "save", "schema",
        "securityaudit", "select", "semantickeyphrasetable", "semanticsimilaritydetailstable",
        "semanticsimilaritytable", "session_user", "set", "setuser", "shutdown", "some",
        "statistics", "system_user", "table", "tablesample", "textsize", "then",
        "to", "top", "tran", "transaction", "trigger", "truncate",
        "try_convert", "tsequal", "union", "unique", "unpivot", "update",
        "updatetext", "use", "user", "values", "varying", "view",
        "waitfor", "when", "where", "while", "with", "writetext",
    };

    /// <summary>True when <paramref name="identifier"/> must be bracketed to mean itself: it is empty,
    /// it is not a regular identifier, or it collides with a reserved keyword. Case is <em>not</em> a
    /// reason — SQL Server preserves it.</summary>
    public static bool NeedsQuoting(string identifier)
        => identifier.Length == 0 || !BareIdentifierRegex().IsMatch(identifier) || MustQuote.Contains(identifier);

    /// <summary>Unconditionally bracket, escaping a closing bracket by doubling it
    /// (<c>a]b</c> → <c>[a]]b]</c>) — the only escape T-SQL defines inside <c>[ … ]</c>.</summary>
    public static string Quote(string identifier)
        => "[" + identifier.Replace("]", "]]") + "]";

    /// <summary>Bracket only when <see cref="NeedsQuoting"/> says the bare form would not round-trip.</summary>
    public static string QuoteIfNeeded(string identifier)
        => NeedsQuoting(identifier) ? Quote(identifier) : identifier;

    /// <summary>
    /// Strip one layer of delimiting. Both forms are accepted because a T-SQL script may use either:
    /// brackets always, and double quotes when <c>QUOTED_IDENTIFIER</c> is ON (which it is for every
    /// client this app could be). Unescapes <c>]]</c> and <c>""</c> respectively.
    /// </summary>
    public static string Unquote(string identifier)
    {
        if (identifier.Length >= 2 && identifier[0] == '[' && identifier[^1] == ']')
            return identifier[1..^1].Replace("]]", "]");
        if (identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"')
            return identifier[1..^1].Replace("\"\"", "\"");
        return identifier;
    }

    /// <summary>
    /// A regular T-SQL identifier: a letter or underscore, then letters, digits, <c>_</c>, <c>@</c>,
    /// <c>$</c> or <c>#</c>. The documented rule also allows a leading <c>@</c> (a variable) or
    /// <c>#</c> (a temp table); neither can name a catalog object, so a name starting with one is
    /// bracketed rather than emitted as something the parser reads as a different kind of thing.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_@$#]*$")]
    private static partial Regex BareIdentifierRegex();
}
