namespace Bearing.Sql;

/// <summary>
/// How an engine spells the literal forms the two do not share. Everything else — single-quoting, ISO
/// dates, invariant numbers — is identical, which is why this is a two-member enum rather than a second
/// renderer. Resolved per connection through <c>ProviderTraits</c>.
/// <para>
/// Here rather than beside the renderer that reads it (<c>Bearing.App.Results.SqlValue</c>) because it is a
/// fact about an engine's SQL <i>text</i>, which is this project's subject — and because
/// <c>ProviderTraits</c>, which pairs it with the connection's dialect, is no longer in the App layer.
/// </para>
/// </summary>
public enum SqlLiteralStyle
{
    /// <summary>PostgreSQL: the <c>true</c>/<c>false</c> keywords, and bytea as a quoted <c>'\x…'</c>.</summary>
    Postgres,

    /// <summary>T-SQL: no boolean literal at all (<c>bit</c> takes <c>1</c>/<c>0</c>), and binary as the
    /// bare <c>0x…</c> constant — quoting that would make it a string.</summary>
    TSql,

    /// <summary>MySQL / MariaDB: <c>true</c>/<c>false</c> (aliases of 1/0), binary as the bare <c>0x…</c>
    /// constant, and a string literal that also escapes the <b>backslash</b> — which both engines read as an
    /// escape under the default <c>sql_mode</c>, so a value ending in <c>\</c> would otherwise escape its own
    /// closing quote and run the rest of the line as SQL.</summary>
    MySql,
}
