namespace Bearing.Sql;

/// <summary>
/// The fixed set of SQL expressions an inline grid edit may stand for on <b>SQL Server</b> (#149) —
/// <see cref="EditExpression"/>'s counterpart, and the same relationship <see cref="TSqlWriteGuard"/> has
/// to <see cref="WriteGuard"/>: a table of its own, one shared mechanism.
/// <para>
/// A separate table rather than a translation. <c>now()</c> typed on a SQL Server connection stays refused
/// and stays amber, because it is not something this engine can evaluate — silently rewriting it to
/// <c>getdate()</c> would mean the statement that ran was not the one the cell showed, which is the
/// property the whole design of <see cref="EditExpression"/> exists to keep. Only <c>current_timestamp</c>,
/// <c>current_user</c>, <c>session_user</c> and <c>default</c> are common to both engines, and they are
/// common because T-SQL genuinely spells them that way.
/// </para>
/// <para>
/// Everything in <see cref="EditExpression"/>'s remarks applies here unchanged: the two conditions that
/// bound where a substitution may happen, the canonical spelling being ours rather than the user's, and
/// the argument-less rule that stops one carrying a subquery or a second statement.
/// </para>
/// </summary>
public static class TSqlEditExpression
{
    /// <summary>Canonical spellings, keyed by their normalized form. Argument-less by construction — a
    /// niladic function (<c>current_timestamp</c>) takes no parentheses in T-SQL and must not be given
    /// any, which is why those entries have none.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        // Clock. getdate/getutcdate are the datetime pair; the sys* three are the datetime2 /
        // datetimeoffset ones and are what a modern column wants.
        ["getdate()"] = "getdate()",
        ["getutcdate()"] = "getutcdate()",
        ["sysdatetime()"] = "sysdatetime()",
        ["sysutcdatetime()"] = "sysutcdatetime()",
        ["sysdatetimeoffset()"] = "sysdatetimeoffset()",
        ["current_timestamp"] = "current_timestamp",
        // Identity. system_user is the login, current_user/session_user the database user — T-SQL
        // distinguishes them and so does this table; collapsing them would answer a different question.
        ["current_user"] = "current_user",
        ["session_user"] = "session_user",
        ["system_user"] = "system_user",
        ["suser_sname()"] = "suser_sname()",
        // Where we are. The counterparts of Postgres' current_database() / current_schema().
        ["db_name()"] = "db_name()",
        ["schema_name()"] = "schema_name()",
        // Keys.
        ["newid()"] = "newid()",
        // Valid bare in both `set c = default` and `values (default)`, as on Postgres.
        ["default"] = "default",
    };

    /// <summary>The canonical T-SQL for <paramref name="text"/>, or null when it is not one of the known
    /// expressions. Case- and whitespace-insensitive; the returned string comes from <see cref="Known"/>.</summary>
    public static string? TryRecognize(string? text) => EditExpression.Lookup(Known, text);

    /// <inheritdoc cref="EditExpression.All"/>
    public static IReadOnlyCollection<string> All => Known.Values;

    /// <summary>
    /// The short list a menu offers, in menu order — <see cref="EditExpression.Offered"/>'s counterpart,
    /// and the same rule: offering is a stronger claim than accepting, so a near-duplicate that a menu
    /// cannot explain stays typeable instead.
    /// <para>
    /// The clock family is where that bites here. <c>getdate()</c> is the one in every T-SQL codebase and
    /// <c>sysdatetimeoffset()</c> is the only member that answers a <em>different</em> question — it carries
    /// the offset, which is the whole point of a <c>datetimeoffset</c> column. <c>sysdatetime()</c>,
    /// <c>sysutcdatetime()</c> and <c>getutcdate()</c> differ from those two in precision or in zone, which
    /// is a choice worth making deliberately by typing it rather than by picking the third item down.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Offered { get; } =
    [
        Known["getdate()"],
        Known["sysdatetimeoffset()"],
        Known["current_user"],
        Known["newid()"],
        Known["default"],
    ];
}
