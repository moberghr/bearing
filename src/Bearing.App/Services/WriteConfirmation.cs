using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.Core.Data;
using Bearing.Sql;

namespace Bearing.App.Services;

/// <summary>Which write is being confirmed — it decides how the prompt reads and what its button says.</summary>
public enum WriteAction
{
    /// <summary>A SQL batch the user submitted from the editor.</summary>
    RunBatch,

    /// <summary>The generated DML behind a result-grid save.</summary>
    SaveEdits,
}

/// <summary>One statement as a write confirmation lists it: a kind tag (<c>UPDATE</c>, <c>DROP + CREATE</c>,
/// or a plain <c>SELECT</c> for a read that shares the batch), the SQL itself, and whether it writes.</summary>
/// <param name="Impact">How many rows this statement will touch (#112), when that could be established.
/// Null for everything else — most statements, including every statement the reducer declines.</param>
public sealed record WriteStatement(string Kind, string Sql, bool IsRisky, RowImpact? Impact = null);

/// <summary>
/// How many rows one <c>UPDATE</c> / <c>DELETE</c> is about to touch (#112), and #100's "no WHERE at all".
/// <para>
/// 3,412 when you expected 1 is unmistakable in a way no amount of "are you sure" is — it is the number
/// that catches the wrong-tab mistake, because the tab you meant had one matching row and this one has the
/// whole table. Three states, and they are deliberately distinguishable: a count, "every row", and
/// <em>we could not find out</em>. The last is never rendered as zero or as silence.
/// </para>
/// </summary>
/// <param name="Verb"><c>UPDATE</c> or <c>DELETE</c>.</param>
/// <param name="Relation">The relation as the statement names it.</param>
/// <param name="Rows">The count, or null when it could not be taken in time.</param>
/// <param name="EveryRow">True when the statement has no <c>WHERE</c> — #100's case.</param>
public sealed record RowImpact(string Verb, string Relation, long? Rows, bool EveryRow)
{
    /// <summary>The statement matched a predicate, and this many rows satisfy it right now.</summary>
    public static RowImpact Counted(UpdateDeleteTarget target, long rows)
        => new(target.Verb, target.Relation, rows, EveryRow: false);

    /// <summary>No <c>WHERE</c>: the whole table (#100). No count is taken — the answer is already known,
    /// and it is not a number the user needs.</summary>
    public static RowImpact Every(UpdateDeleteTarget target)
        => new(target.Verb, target.Relation, null, EveryRow: true);

    /// <summary>The count did not come back in its budget. Said out loud rather than dropped, so the absence
    /// of a number can't be read as a small one.</summary>
    public static RowImpact Uncounted(UpdateDeleteTarget target)
        => new(target.Verb, target.Relation, null, EveryRow: false);

    /// <summary>What the prompt says. Invariant grouping, for the reason the plan window uses it: a prompt
    /// that reads "3.412 rows" to one user and "3,412" to another is a prompt about a different number.</summary>
    public string Text => EveryRow
        ? $"This will {Action} every row in {Relation}."
        : Rows switch
        {
            1 => $"This will {Action} 1 row in {Relation}.",
            { } n => $"This will {Action} {n.ToString("N0", CultureInfo.InvariantCulture)} rows in {Relation}.",
            null => $"Could not count the rows this will {Action} in {Relation} in time.",
        };

    /// <summary>Whether to draw this loud. Every row is the alarming case — it is #100's warning — and so is
    /// a count nobody could take, because the user is then deciding blind.</summary>
    public bool IsAlarming => EveryRow || Rows is null;

    private string Action => Verb.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ? "delete" : "update";
}

/// <summary>
/// Everything a write confirmation needs to be answerable without leaving the dialog: the target connection,
/// what kind of write it is, the risky verbs, and the actual statements about to run. The statements are the
/// point — a yes/no prompt can't answer "am I about to nuke prod", so both write paths (editor batch and
/// inline-edit save) build one of these and hand it to <see cref="IDialogService.ConfirmWriteAsync"/>.
/// Pure: all display text is derived here so it can be tested without a window (§2.5, §4.3).
/// </summary>
/// <param name="GuardIsDialectAware">
/// False when <see cref="Bearing.Sql.WriteGuard"/> could not read this engine's grammar
/// (<see cref="Bearing.Sql.ISqlDialect.HasDialectAwareGuard"/>) and therefore reported every statement as
/// risky rather than guess. It changes what the prompt may claim: without it, every run of a guarded
/// connection confirms and the user reads the standard wording as "your SELECT is destructive", which is
/// both untrue and the fastest way to teach someone to click through the guard. That was SQL Server's
/// situation until its guard learned to read T-SQL; both shipped dialects report true now, so this arm is
/// what the next engine gets before its own scanner exists. Defaults to true so the Postgres path — and
/// every existing caller — is untouched.
/// </param>
public sealed record WriteConfirmation(
    ConnectionInfo Connection,
    WriteAction Action,
    IReadOnlyList<string> Verbs,
    IReadOnlyList<WriteStatement> Statements,
    bool GuardIsDialectAware = true)
{
    /// <summary>A submitted batch: every statement, reads included, each tagged with what it does.</summary>
    /// <param name="impacts">
    /// Row counts by statement index (#112), for the statements a count could be taken for. Optional so the
    /// no-connection and count-declined paths build the same record they always did.
    /// </param>
    public static WriteConfirmation ForBatch(
        ConnectionInfo connection,
        IReadOnlyList<StatementRisk> statements,
        IReadOnlyDictionary<int, RowImpact>? impacts = null)
        => new(connection, WriteAction.RunBatch,
            statements.SelectMany(s => s.RiskyVerbs).Distinct(StringComparer.Ordinal).ToList(),
            statements
                .Select((s, i) => new WriteStatement(
                    s.Label, s.Text, s.IsRisky, impacts is not null && impacts.TryGetValue(i, out var im) ? im : null))
                .ToList(),
            // One statement that the guard could not read makes the whole verdict unreliable, so the
            // conservative reading wins for the batch. An empty batch has nothing to be unsure about.
            GuardIsDialectAware: statements.Count == 0 || statements.All(s => s.GuardIsDialectAware));

    /// <summary>An inline-edit save: the generated DML, which is risky by definition.</summary>
    public static WriteConfirmation ForEdits(ConnectionInfo connection, IReadOnlyList<WriteStatement> changes)
        => new(connection, WriteAction.SaveEdits,
            changes.Select(c => c.Kind).Distinct(StringComparer.Ordinal).ToList(), changes);

    /// <summary>True when the connection itself demands confirmation (the Production preset) — as opposed to
    /// an inline save, which always confirms. Drives the extra warning line.</summary>
    public bool IsGuarded => Connection.RequireWriteConfirmation;

    /// <summary>Statements that write data or alter schema. For a save that's all of them.</summary>
    public int RiskyCount => Statements.Count(s => s.IsRisky);

    public string Title => Action == WriteAction.RunBatch ? "Confirm write" : "Confirm save";

    /// <summary>Connection name plus its environment label, so the prompt names where this lands.</summary>
    public string Target => string.IsNullOrWhiteSpace(Connection.Environment)
        ? Connection.Name
        : $"{Connection.Name} · {Connection.Environment}";

    public string Heading => Action == WriteAction.RunBatch
        ? $"Run on {Target}?"
        : $"Save {Plural(Statements.Count, "change")} to {Target}?";

    /// <summary>What is about to happen, in one line. Names the verbs for a batch (and says how much of it
    /// only reads), and the transaction guarantee for a save.</summary>
    public string Summary => Action switch
    {
        // Ahead of the write-counting arms: when the guard couldn't read the dialect, RiskyCount is every
        // statement by fiat, and saying "3 statements will modify data or schema" of three SELECTs would be
        // a straight falsehood.
        WriteAction.RunBatch when !GuardIsDialectAware =>
            $"{Plural(Statements.Count, "statement")} below will run on {Target}."
            + (Verbs.Count > 0
                ? $" Recognised as writes: {VerbList}."
                : " None of them was recognised as a write."),
        WriteAction.RunBatch when Statements.Count == RiskyCount =>
            $"{Plural(Statements.Count, "statement")} below will modify data or schema ({VerbList}).",
        WriteAction.RunBatch =>
            $"{RiskyCount} of the {Statements.Count} statements below will modify data or schema "
            + $"({VerbList}); the rest only read.",
        _ when Statements.Count == 1 =>
            "1 statement runs as one transaction — if it fails, nothing is committed.",
        _ => $"{Statements.Count} statements run as one transaction — if any of them fails, "
            + "none of the changes are committed.",
    };

    /// <summary>
    /// The row counts, in statement order (#112). Empty when nothing could be counted — which is the common
    /// case, and reads exactly as the confirmation did before.
    /// </summary>
    public IReadOnlyList<RowImpact> Impacts
        => Statements.Select(s => s.Impact).OfType<RowImpact>().ToList();

    /// <summary>
    /// The caveat a multi-write batch needs: the counts were taken before <em>any</em> of it ran, so a
    /// statement that a later one feeds is counted against rows an earlier one may remove. Null for a single
    /// write, where there is nothing ahead of it to change the answer.
    /// </summary>
    public string? ImpactCaveat => Impacts.Count > 0 && RiskyCount > 1
        ? "Counted before the batch runs — an earlier statement can change what a later one matches."
        : null;

    /// <summary>The loud line shown only for a connection marked as requiring write confirmation; null on an
    /// ordinary connection, where an inline save still confirms but doesn't need shouting about.</summary>
    public string? Warning => IsGuarded
        ? $"⚠ {Connection.Name} is marked as requiring confirmation for every write."
        : null;

    /// <summary>
    /// Why a read is being confirmed at all; null whenever the guard actually understood the batch.
    /// <para>
    /// This is the honest half of failing safe (§1.2). The guard errs toward asking, which is right — but a
    /// prompt that asks without saying why trains the user to dismiss it, and the next prompt it dismisses
    /// will be a real DROP. So it names the limitation instead of implying the statements write.
    /// </para>
    /// </summary>
    public string? GuardNote => GuardIsDialectAware
        ? null
        : "Bearing does not parse this engine's SQL yet, so it cannot tell a read from a write here. "
        + "Every statement in the batch is listed and confirmed. This is the guard being cautious, "
        + "not a finding about these statements.";

    public string ConfirmLabel => Action == WriteAction.RunBatch ? "Run anyway" : "✓ Save";

    /// <summary>All statements as one copyable script. Batch statements carry whatever terminator the user
    /// typed (a blank-line-separated statement has none), so one is added where it's missing.</summary>
    public string Script
        => string.Join("\n", Statements.Select(s => s.Sql.EndsWith(';') ? s.Sql : s.Sql + ";"));

    private string VerbList => string.Join(", ", Verbs);

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
