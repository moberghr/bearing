using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Data;
using Bearing.Sql;

namespace Bearing.App.Services;

/// <summary>
/// Whether a batch is refused outright because its connection is read-only (#99), and the sentence that says
/// so. The counterpart to <see cref="WriteConfirmation"/> and deliberately a different shape: that one builds
/// a dialog to ask a question, this one answers it. A read-only connection has nothing to confirm.
/// <para>
/// Pure, and in the App layer rather than <c>Core</c> because it reads <see cref="StatementRisk"/> from
/// <c>Bearing.Sql</c>, which <c>Core</c> may not reference (§2.2). Tested without a window, like
/// <see cref="WriteConfirmation"/>.
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
