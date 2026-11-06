namespace Bearing.Sql;

/// <summary>
/// The fixed set of SQL expressions an inline grid edit may stand for on <b>MySQL and MariaDB</b> (#149) —
/// <see cref="EditExpression"/>'s and <see cref="TSqlEditExpression"/>'s counterpart, a table of its own
/// rather than a translation (§5.4a). Every entry is spelled the same way on both engines; an expression
/// only one of them evaluates (MariaDB's <c>sys_guid()</c>, MySQL's <c>uuid_to_bin()</c>) is left out,
/// because the dialect is shared and the cell must not claim something half the servers reject.
/// </summary>
public static class MySqlEditExpression
{
    /// <summary>Canonical spellings, keyed by their normalized form. Argument-less by construction.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        // Clock. now() and current_timestamp are synonyms here (statement start); sysdate() is the
        // wall clock at evaluation and differs, so it stays typeable rather than offered.
        ["now()"] = "now()",
        ["current_timestamp"] = "current_timestamp",
        ["current_timestamp()"] = "current_timestamp()",
        ["sysdate()"] = "sysdate()",
        ["utc_timestamp()"] = "utc_timestamp()",
        ["current_date"] = "current_date",
        ["curdate()"] = "curdate()",
        ["current_time"] = "current_time",
        ["curtime()"] = "curtime()",
        ["localtimestamp"] = "localtimestamp",
        // Identity: current_user is the account the server authenticated, user() the one the client asked for.
        ["current_user"] = "current_user",
        ["current_user()"] = "current_user()",
        ["user()"] = "user()",
        // Where we are.
        ["database()"] = "database()",
        // Keys.
        ["uuid()"] = "uuid()",
        ["default"] = "default",
    };

    /// <summary>The canonical MySQL for <paramref name="text"/>, or null when it is not a known expression.</summary>
    public static string? TryRecognize(string? text) => EditExpression.Lookup(Known, text);

    /// <inheritdoc cref="EditExpression.All"/>
    public static IReadOnlyCollection<string> All => Known.Values;

    /// <summary>The short list a menu offers, in menu order — the same rule as
    /// <see cref="EditExpression.Offered"/>: one member per question, near-duplicates stay typeable.</summary>
    public static IReadOnlyList<string> Offered { get; } =
    [
        Known["now()"],
        Known["current_date"],
        Known["current_user"],
        Known["uuid()"],
        Known["default"],
    ];
}
