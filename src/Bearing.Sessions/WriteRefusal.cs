using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Data;
using Bearing.Sql;

namespace Bearing.Sessions;

/// <summary>
/// Whether a batch is refused outright because its connection is read-only (#99), and the sentence that says
/// so. The counterpart to <c>WriteConfirmation</c> and deliberately a different shape: that one builds
/// a dialog to ask a question, this one answers it. A read-only connection has nothing to confirm.
/// <para>
/// Pure, and here rather than in <c>Core</c> because it reads <see cref="StatementRisk"/> from
/// <c>Bearing.Sql</c>, which <c>Core</c> may not reference (§2.2). Beside the sessions it judges rather than
/// beside the dialog that asks, because the refusal has to be the same one wherever the statement came from:
/// the editor, an inline-grid save, or a host with no window at all. Tested without a window, like
/// <c>WriteConfirmation</c>.
/// </para>
/// <para>
/// This is the <em>early</em> half of #99 and not the enforcement: the server's
/// <c>default_transaction_read_only</c> is what actually refuses, and it catches what a lexer cannot. What
/// this adds is refusing before the round trip, so the user gets a sentence naming their own setting instead
/// of a <c>25006</c> from the server. Both halves are wanted — neither is a substitute for the other.
/// </para>
/// </summary>
public static class WriteRefusal
{
    /// <summary>
    /// Why this batch will not be run, or null when it may be. Null for every read, and for every statement
    /// on a connection that is not read-only.
    /// <para>
    /// Independent of <see cref="ConnectionInfo.RequireWriteConfirmation"/> on purpose: the two settings
    /// answer different questions, and a read-only connection refuses whether or not it also asks. The write
    /// guard is not narrowed by any of this (§1.2) — this reads the verdict it already produces.
    /// </para>
    /// </summary>
    public static string? Reason(ConnectionInfo connection, IReadOnlyList<StatementRisk> statements)
    {
        if (!SessionPolicy.IsReadOnly(connection)) return null;

        var verbs = statements.Where(s => s.IsRisky)
            .Select(s => s.Label)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (verbs.Count == 0) return null;

        return $"{connection.Name} is read-only — {string.Join(", ", verbs)} not run. "
               + "Turn read-only off for this connection to write to it.";
    }

    /// <summary>
    /// Why a batch containing <c>BEGIN</c> / <c>COMMIT</c> / <c>ROLLBACK</c> / <c>SAVEPOINT</c> will not be
    /// run on a manual-commit connection, or null when it may be (#131).
    /// <para>
    /// Only on a manual-commit connection: an ordinary one keeps running a script that manages its own
    /// transactions exactly as it always has, so this narrows nothing (§1.2). What it protects is the one
    /// case where Bearing is itself holding a transaction on that connection — a raw <c>COMMIT</c> there
    /// ends it behind the driver's back, leaving the transaction object claiming something the server no
    /// longer has, the chip counting statements into nothing, and Commit throwing when the user presses it.
    /// </para>
    /// <para>
    /// Explicit control is refused for the whole batch, and whether or not a transaction is open yet. "Who
    /// owns the transaction on this connection" has one answer on a manual-commit connection, and making it
    /// conditional on whether one happens to be open right now is the ambiguity the refusal exists to remove.
    /// </para>
    /// <para>
    /// A statement that only <em>commits implicitly</em> (<see cref="StatementRisk.CommitsImplicitly"/>) is
    /// different: it is not about the transaction at all, and with none held it commits nothing but itself.
    /// It is refused only when <paramref name="transactionHeld"/>, when a block that runs as it is sent mixes it
    /// with writes (<see cref="StatementRisk.MixesWriteWithImplicitCommit"/>), or when another write in the
    /// batch would open one — each with a sentence saying which, never the typed-<c>COMMIT</c> one.
    /// </para>
    /// </summary>
    public static string? ReasonForTransactionControl(
        ConnectionInfo connection, IReadOnlyList<StatementRisk> statements, bool transactionHeld)
    {
        if (!CommitPolicy.IsManualCommit(connection))
        {
            return null;
        }

        var explicitControl = statements.Where(s => s.IsTransactionControl && !s.CommitsImplicitly)
            .Select(s => s.TransactionControl!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (explicitControl.Count > 0)
        {
            return $"{connection.Name} is in manual-commit mode — {string.Join(", ", explicitControl)} not run. "
                   + "Bearing holds the transaction on this connection; use Commit or Rollback to end it.";
        }

        // A statement that only commits implicitly (MySQL DDL, LOCK TABLES, …) is no business of the
        // transaction's while there is none: run alone with nothing held, it opens nothing
        // (OpensTransaction) and commits only itself. It is refused only where it would end one — a
        // transaction this tab already holds, or the one the other writes in its batch would open.
        var implicitCommits = statements.Where(s => s.CommitsImplicitly)
            .Select(s => s.TransactionControl ?? s.Verb)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (implicitCommits.Count == 0)
        {
            return null;
        }
        var named = string.Join(", ", implicitCommits.Select(StripImplicitNote));
        if (transactionHeld)
        {
            return $"{connection.Name} is in manual-commit mode — {named} not run: it commits implicitly and "
                   + "would end the transaction Bearing holds. Commit or Rollback first, then run it.";
        }
        if (statements.Any(s => s.MixesWriteWithImplicitCommit))
        {
            return $"{connection.Name} is in manual-commit mode — {named} not run: it commits implicitly inside a "
                   + "block that also writes, so the block's writes would commit at once instead of waiting for "
                   + "Commit. Run the writes and the statement that commits implicitly separately.";
        }
        if (statements.Any(OpensTransaction))
        {
            return $"{connection.Name} is in manual-commit mode — {named} not run: it commits implicitly, so the "
                   + "other writes in this batch would not stay in the transaction Bearing opens for them. "
                   + "Run it on its own.";
        }
        return null;
    }

    /// <summary>
    /// Whether this statement, run on a manual-commit connection with no transaction held, makes Bearing open
    /// one (#131): it names a write — and does not commit implicitly, since a transaction opened for a
    /// statement that commits it straight away would leave Bearing holding one the server no longer has.
    /// </summary>
    public static bool OpensTransaction(StatementRisk statement)
        => statement.NamesAWrite && !statement.CommitsImplicitly;

    private static string StripImplicitNote(string label)
    {
        var note = $" ({StatementRisk.ImplicitCommitNote})";
        return label.EndsWith(note, StringComparison.Ordinal) ? label[..^note.Length] : label;
    }

    /// <summary>The same refusal for an inline-grid save, whose statements are writes by construction so
    /// there is no risk verdict to read. A backstop: the grid does not offer an edit on a read-only
    /// connection at all (its lock chip says why), so reaching this means something upstream let one
    /// through.</summary>
    public static string? ReasonForEdits(ConnectionInfo connection)
        => SessionPolicy.IsReadOnly(connection)
            ? $"{connection.Name} is read-only — changes not saved. "
              + "Turn read-only off for this connection to write to it."
            : null;

    /// <summary>
    /// The refusal for terminating a backend from the activity panel (#101).
    /// <para>
    /// This one is ours and cannot be delegated to the server, which is the whole reason it exists:
    /// <c>pg_terminate_backend</c> is a function call rather than a transactional write, so
    /// <c>default_transaction_read_only</c> lets it through and a connection the user marked read-only would
    /// happily drop someone's session. Read-only means "I cannot change this server", and ending a session is
    /// a change to it.
    /// </para>
    /// <para>
    /// <b>Cancel is deliberately not refused</b> — it stops a statement and leaves the session, which is the
    /// hung-query escape hatch #101 exists for, and it is most wanted on exactly the production connection
    /// most likely to be read-only. The asymmetry is the decision, not an oversight.
    /// </para>
    /// </summary>
    public static string? ReasonForTerminate(ConnectionInfo connection)
        => SessionPolicy.IsReadOnly(connection)
            ? $"{connection.Name} is read-only — session not terminated. "
              + "Cancelling a statement still works; turn read-only off to end a session."
            : null;
}
