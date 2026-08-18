using System.Text.RegularExpressions;

namespace Bearing.Sql;

/// <summary>
/// Postgres identifier quoting — the one place that answers "does this catalog name survive being
/// typed into SQL bare?". Postgres folds every unquoted identifier to lower case, so
/// <c>__MigrationHistory</c> must be written <c>"__MigrationHistory"</c>, and a relation named
/// <c>order</c> needs quotes because the parser would read the keyword.
/// <para>
/// Quoting is <em>conditional</em> here, unlike the always-quote helpers in
/// <see cref="TableDdlGenerator"/> / <see cref="DmlGenerator"/> / <c>ResultEditModel</c> (which
/// generate SQL nobody reads): completion output is read and typed over, so
/// <c>select * from "film" f</c> would be a regression in feel.
/// </para>
/// </summary>
public static partial class PgIdentifier
{
    /// <summary>
    /// Keywords that cannot appear as a bare table/column name. Postgres' <c>ColId</c> (the production
    /// behind a relation or column name) admits identifiers, <c>unreserved_keyword</c> and
    /// <c>col_name_keyword</c> — so only the other two categories force quotes. Generated from
    /// <c>reserved_keyword</c> + <c>type_func_name_keyword</c> in the vendored PostgreSQLParser.g4
    /// (the <c>_P</c> token suffix stripped).
    /// </summary>
    private static readonly HashSet<string> MustQuote = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "analyse", "analyze", "and", "any", "array",
        "as", "asc", "asymmetric", "authorization", "binary", "both",
        "case", "cast", "check", "collate", "collation", "column",
        "concurrently", "constraint", "create", "cross", "current_catalog", "current_date",
        "current_role", "current_schema", "current_time", "current_timestamp", "current_user", "default",
        "deferrable", "desc", "distinct", "do", "else", "end",
        "except", "false", "fetch", "for", "foreign", "freeze",
        "from", "full", "grant", "group", "having", "ilike",
        "in", "initially", "inner", "intersect", "into", "is",
        "isnull", "join", "lateral", "leading", "left", "like",
        "limit", "localtime", "localtimestamp", "natural", "not", "notnull",
        "null", "offset", "on", "only", "or", "order",
        "outer", "overlaps", "placing", "primary", "references", "returning",
        "right", "select", "session_user", "similar", "some", "symmetric",
        "system_user", "table", "tablesample", "then", "to", "trailing",
        "true", "union", "unique", "user", "using", "variadic",
        "verbose", "when", "where", "window", "with",
    };

    /// <summary>True when <paramref name="identifier"/> must be double-quoted to mean itself: it isn't
    /// all-lowercase <c>[a-z_][a-z0-9_$]*</c>, or it collides with a keyword Postgres won't read as a name.</summary>
    public static bool NeedsQuoting(string identifier)
        => identifier.Length == 0 || !BareIdentifierRegex().IsMatch(identifier) || MustQuote.Contains(identifier);

    /// <summary>Unconditionally double-quote, escaping embedded quotes (<c>a"b</c> → <c>"a""b"</c>).</summary>
    public static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Quote only when <see cref="NeedsQuoting"/> says the bare form would not round-trip.</summary>
    public static string QuoteIfNeeded(string identifier)
        => NeedsQuoting(identifier) ? Quote(identifier) : identifier;

    /// <summary>Strip surrounding double quotes and unescape doubled ones — the inverse of <see cref="Quote"/>.</summary>
    public static string Unquote(string identifier)
        => identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"'
            ? identifier[1..^1].Replace("\"\"", "\"")
            : identifier;

    /// <summary>An identifier Postgres reads as itself without quotes: lower-case, no leading digit.</summary>
    [GeneratedRegex(@"^[a-z_][a-z0-9_$]*$")]
    private static partial Regex BareIdentifierRegex();
}
