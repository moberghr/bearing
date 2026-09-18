using System.Text.RegularExpressions;

namespace Bearing.Sql;

/// <summary>
/// The fixed set of SQL expressions an inline grid edit may stand for (#149) — <c>now()</c>,
/// <c>current_timestamp</c>, <c>gen_random_uuid()</c>, <c>default</c> and the rest of the list below.
/// Pure and engine-specific, which is why it lives here rather than beside the grid.
/// <para>
/// <b>What makes this safe is not the list, it is where it is consulted.</b> A recognised expression is only
/// ever substituted for a cell value that (a) sits in a column the driver does not map to
/// <see cref="string"/>, and (b) failed every literal parse <c>ResultEditModel.Coerce</c> offers — that is,
/// a value that today cannot be written at all and is already drawn amber. Reinterpreting it therefore
/// cannot change the meaning of any edit that works now. A <c>text</c> column holding the literal string
/// <c>now()</c> is a legitimate value and is never touched, because <c>Coerce</c> accepts it.
/// </para>
/// <para>
/// <b>The emitted SQL is ours, never the user's.</b> <see cref="TryRecognize"/> returns the canonical
/// spelling from this file's own table, so nothing the user typed is interpolated into a statement — which
/// is what keeps §5.4's "every value is a parameter" rule broken in exactly one bounded place. Nothing here
/// takes an argument, so nothing here can carry a subquery or a second statement.
/// </para>
/// </summary>
public static class EditExpression
{
    /// <summary>Canonical spellings, keyed by their normalized form. Argument-less by construction — see the
    /// class remarks for why that is a rule rather than a coincidence.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["now()"] = "now()",
        ["current_timestamp"] = "current_timestamp",
        ["current_date"] = "current_date",
        ["current_time"] = "current_time",
        ["localtimestamp"] = "localtimestamp",
        ["localtime"] = "localtime",
        ["clock_timestamp()"] = "clock_timestamp()",
        ["statement_timestamp()"] = "statement_timestamp()",
        ["transaction_timestamp()"] = "transaction_timestamp()",
        ["current_user"] = "current_user",
        ["session_user"] = "session_user",
        ["current_database()"] = "current_database()",
        ["current_schema()"] = "current_schema()",
        ["gen_random_uuid()"] = "gen_random_uuid()",
        ["uuid_generate_v4()"] = "uuid_generate_v4()",
        // Valid bare in both `set c = default` and `values (default)`, which is the point of it.
        ["default"] = "default",
    };

    /// <summary>Whitespace inside and around an empty argument list is cosmetic: <c>now ( )</c> is <c>now()</c>.</summary>
    private static readonly Regex EmptyCall = new(@"\s*\(\s*\)$", RegexOptions.Compiled);

    /// <summary>The canonical SQL for <paramref name="text"/>, or null when it is not one of the known
    /// expressions. Case- and whitespace-insensitive; the returned string comes from <see cref="Known"/>.</summary>
    public static string? TryRecognize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = EmptyCall.Replace(text.Trim().ToLowerInvariant(), "()");
        return Known.TryGetValue(normalized, out var sql) ? sql : null;
    }

    /// <summary>Every expression the grid accepts, canonically spelled — for anything that has to *list* them
    /// (a tooltip, a menu, documentation) rather than test one.</summary>
    public static IReadOnlyCollection<string> All => Known.Values;

    /// <summary>
    /// The short list a menu offers, in menu order — a subset of <see cref="All"/>, which stays typeable in
    /// full.
    /// <para>
    /// Offering is a stronger claim than accepting. A cell accepts anything in the table because the user
    /// typed it and meant it; a menu item says "this works here", so the list leaves out the ones that would
    /// make that a lie or a coin toss:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>uuid_generate_v4()</c> needs the <c>uuid-ossp</c> extension installed. Typing it is the
    ///     user's own claim about their server; putting it in a menu would be ours, and wrong on a stock one
    ///     — which is why <c>gen_random_uuid()</c> (core since 13) is the one offered.</item>
    ///   <item>the near-duplicates — <c>current_timestamp</c>, <c>localtimestamp</c>,
    ///     <c>clock_timestamp()</c>, <c>statement_timestamp()</c>, <c>transaction_timestamp()</c> — differ
    ///     from <c>now()</c> in ways (transaction versus statement versus wall clock, zone or no zone) that a
    ///     menu cannot explain and a user picking from one should not have to guess at. Anyone who wants a
    ///     specific one knows its name and can type it.</item>
    /// </list>
    /// <para>
    /// Drawn from <see cref="Known"/> rather than written out again, so a spelling cannot drift between what
    /// the menu writes and what the recogniser reads — they are the same string.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Offered { get; } =
    [
        Known["now()"],
        Known["current_date"],
        Known["current_user"],
        Known["gen_random_uuid()"],
        Known["default"],
    ];
}
