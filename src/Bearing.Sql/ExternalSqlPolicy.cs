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

            if (DeniedRelation(dialect, statement.Text) is { } relation)
                return $"'{relation}' is not readable through an exposed connection. It holds credentials — "
                     + "a password hash, or a connection string for another server (§1.8).";
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
        => Scan(dialect, statement) is { } scan
            ? First(dialect.ExternalDeniedFunctions, scan.Called)
            : FallbackByText(dialect.ExternalDeniedFunctions, statement);

    /// <summary>
    /// The first credential-bearing catalog this statement names, or null — the same fence with the same
    /// limits, aimed at a read rather than a call.
    /// <para>
    /// §1.8 already forbids Bearing's own catalog reads from selecting these columns, and an exposed
    /// connection reintroduced every one of them: <c>select umoptions from pg_user_mappings</c> is a plain
    /// SELECT the allow-list admits, and it returns a foreign server's password in cleartext — to the
    /// mapping's owner, so not even the over-privileged connection the other two need.
    /// </para>
    /// </summary>
    public static string? DeniedRelation(ISqlDialect dialect, string statement)
        => Scan(dialect, statement) is { } scan
            ? First(dialect.ExternalDeniedRelations, scan.Mentioned)
            // No text fallback for this one, and the asymmetry is deliberate: a bare name matched in text
            // would fire on the word inside a comment or a string, refusing ordinary reads for a list whose
            // members are not callable shapes. A statement this engine's lexer cannot read is refused
            // upstream anyway — an unreadable dialect vouches for no verb (§1.11a).
            : null;

    private static string? First(IReadOnlySet<string> denied, IReadOnlySet<string> found)
    {
        foreach (var name in denied)
            if (found.Contains(name)) return name;

        return null;
    }

    /// <summary>
    /// The statement's names, lexed — or <b>null</b> when the lexer could not read it at all.
    /// <para>
    /// Null rather than two empty sets, because those are different answers and collapsing them is the
    /// mistake §1.7 names: "this statement calls nothing denied" and "nothing could be read" would then be
    /// indistinguishable, and every caller would have to treat the safe case as the unknown one. Here that
    /// showed up immediately — running the text fallback on a successful empty scan brought back the false
    /// positive the token scan exists to remove, refusing <c>select 'pg_read_file(' as sample</c>.
    /// </para>
    /// </summary>
    private static (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned)? Scan(
        ISqlDialect dialect, string statement)
    {
        try { return dialect.ExternalNameScan(statement); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The text match this used to be, kept underneath the token scan so a lexer that threw cannot quietly
    /// widen what an exposed connection accepts. It cannot see past a comment — which is why it is no longer
    /// the primary — but everything it does catch, it still catches.
    /// </summary>
    private static string? FallbackByText(IReadOnlySet<string> denied, string statement)
    {
        foreach (var name in denied)
        {
            // The name as a call: a word boundary, the name, an optional closing delimiter, then '(' past
            // any whitespace. A column that merely shares the name is not a call and is left alone.
            if (Regex.IsMatch(statement, $@"\b{Regex.Escape(name)}[""\]]?\s*\(", RegexOptions.IgnoreCase))
                return name;
        }

        return null;
    }

    private static string Listed(IReadOnlySet<string> verbs)
        => string.Join(", ", verbs.OrderBy(v => v, StringComparer.Ordinal));
}
