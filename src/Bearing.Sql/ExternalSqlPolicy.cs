using System.Text.RegularExpressions;

namespace Bearing.Sql;

/// <summary>
/// What a host outside Bearing is allowed to send. An <b>allow-list</b>: a statement runs only if its
/// leading keyword is one this engine calls a read, and everything else is refused — including statements
/// nobody has thought about yet.
/// <para>
/// <b>This is the opposite question from <see cref="WriteGuard"/>, and both are needed.</b> That one is a
/// deny-list, and it is right for the editor: it exists to catch a person's mistake on a connection they
/// own, so anything it does not recognise as dangerous is allowed through. For a caller that is not the
/// person at the keyboard, that default is backwards — the cost of a new SQL construct slipping past is not
/// a surprising prompt, it is an unguarded statement on someone's production server.
/// </para>
/// <para>
/// <b>It was written because the alternative was measured and failed.</b> The exposed session is opened
/// read-only, which Postgres enforces — but <c>default_transaction_read_only</c> is a <c>USERSET</c> GUC
/// and the transaction's own mode can be set: <c>begin read write; select nextval('s')</c> and
/// <c>set transaction read write; select nextval('s')</c> both ran a write on a connection exposed
/// read-only, because neither <c>BEGIN</c> nor <c>SET</c> is a risky verb. §1.9 says as much in words —
/// "a user who types SET default_transaction_read_only = off or BEGIN READ WRITE turns it off" — and this
/// is what it means for a caller who may do exactly that.
/// </para>
/// </summary>
public static class ExternalSqlPolicy
{
    /// <summary>
    /// Why this batch will not be run on an exposed connection, or null when every statement in it is a
    /// read this engine recognises.
    /// <para>
    /// Pairs with <c>WriteRefusal.Reason</c> rather than replacing it: that one names the verb of an
    /// outright write in the sentence a caller most needs ("… so UPDATE will not run"), and callers ask it
    /// first for that reason. This one catches what a deny-list cannot — transaction control, session
    /// settings, and any statement shape that is simply not on the list.
    /// </para>
    /// </summary>
    public static string? Refuse(ISqlDialect dialect, string sql)
    {
        var statements = WriteGuard.Describe(dialect, sql);
        if (statements.Count == 0) return "There is no statement here to run.";

        foreach (var statement in statements)
        {
            // Transaction control first: it is the shape that defeated read-only, so it gets a sentence
            // that says why rather than being lumped in with "not a read".
            if (statement.IsTransactionControl)
                return $"'{statement.TransactionControl}' is not allowed on a connection exposed to external "
                     + "tools — it is what would let a read-only session become writable. Send one read.";

            // A write the deny-list actually *named*. NamesAWrite rather than IsRisky, deliberately: the
            // generous verdict treats any unrecognised T-SQL lead word as a write so that it gets
            // confirmed, and saying "'DECLARE' writes" would assert a cause nobody checked (§1.1). A
            // statement like that is still refused — by the allow-list below, in its own words.
            if (statement.NamesAWrite)
                return $"'{statement.Label}' writes, and an exposed connection runs reads only.";

            if (!dialect.ExternalReadVerbs.Contains(statement.Verb))
                return $"'{statement.Verb}' is not a read. A connection exposed to external tools runs only "
                     + $"{Listed(dialect.ExternalReadVerbs)} — anything else is refused, including statements "
                     + "that would change how this session behaves.";

            if (DeniedFunction(dialect, statement.Text) is { } function)
                return $"'{function}' is not available through an exposed connection. It reaches outside the "
                     + "data — the server's files, its other sessions, or this session's own settings.";
        }

        return null;
    }

    /// <summary>
    /// The first denied function this statement calls, or null.
    /// <para>
    /// <b>This stops accidents, not a determined caller, and must never be described as more.</b> A name can
    /// be reached through a wrapper function, a search_path that resolves elsewhere, or SQL built at
    /// runtime, and no scan of the text catches those. What it does do is make the obvious ways to read the
    /// server's filesystem or kill its sessions fail loudly instead of working — and those are exactly the
    /// things an agent does by accident, because they look like ordinary reads. The boundary that holds is a
    /// role without the privilege (§1.11).
    /// </para>
    /// </summary>
    public static string? DeniedFunction(ISqlDialect dialect, string statement)
    {
        foreach (var name in dialect.ExternalDeniedFunctions)
        {
            // The name as a call: a word boundary, the name, an optional closing delimiter, then '(' past
            // any whitespace. A column that merely shares the name is not a call and is left alone.
            //
            // The delimiter is why this is not just \b…\s*\(. A quoted identifier is the *same name*, not an
            // evasion of the kind this list openly does not catch (a wrapper, a search_path, runtime SQL):
            // `select "pg_read_file"('/etc/passwd')` is ordinary Postgres and ran, while the qualified
            // `pg_catalog.pg_read_file(…)` was caught — an asymmetry with no reason behind it. `]` is
            // T-SQL's spelling of the same thing ([xp_cmdshell]).
            if (Regex.IsMatch(statement, $@"\b{Regex.Escape(name)}[""\]]?\s*\(", RegexOptions.IgnoreCase))
                return name;
        }

        return null;
    }

    private static string Listed(IReadOnlySet<string> verbs)
        => string.Join(", ", verbs.OrderBy(v => v, StringComparer.Ordinal));
}
