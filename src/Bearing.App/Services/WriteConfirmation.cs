using System;
using System.Collections.Generic;
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
public sealed record WriteStatement(string Kind, string Sql, bool IsRisky);

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
/// risky rather than guess. It changes what the prompt may claim: without it, a guarded SQL Server
/// connection confirms on every run and the user reads the standard wording as "your SELECT is
/// destructive", which is both untrue and the fastest way to teach someone to click through the guard.
/// Defaults to true so the Postgres path — and every existing caller — is untouched.
/// </param>
public sealed record WriteConfirmation(
    ConnectionInfo Connection,
    WriteAction Action,
    IReadOnlyList<string> Verbs,
    IReadOnlyList<WriteStatement> Statements,
    bool GuardIsDialectAware = true)
{
    /// <summary>A submitted batch: every statement, reads included, each tagged with what it does.</summary>
    public static WriteConfirmation ForBatch(ConnectionInfo connection, IReadOnlyList<StatementRisk> statements)
        => new(connection, WriteAction.RunBatch,
            statements.SelectMany(s => s.RiskyVerbs).Distinct(StringComparer.Ordinal).ToList(),
            statements.Select(s => new WriteStatement(s.Label, s.Text, s.IsRisky)).ToList(),
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
