using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;

namespace Bearing.App.Services;

/// <summary>
/// The paths that would end an uncommitted transaction without the user saying so, and what each one does
/// about it (#131). One place, because the list is the feature: a transaction that survives one of these
/// silently is the failure manual-commit mode exists to prevent, and a transaction rolled back by one of
/// them without a word is the same failure from the other side.
/// <para>
/// Three shapes, and which one a path gets is a judgement about what the user just asked for:
/// </para>
/// <list type="bullet">
///   <item><b>Refuse</b> (<see cref="ReasonToRefuse"/>) — switching a tab's connection or database. The new
///     target is a different pool (§9.4a) and the transaction lives on a connection out of the old one, so
///     there is no version of this that keeps it. Refusing says so and leaves the user in charge.</item>
///   <item><b>Ask</b> (<see cref="ConfirmAsync"/>) — quitting, disconnecting, closing the tab, refreshing a
///     server's metadata. Each is one click away from being an accident, and each is fine to do once the
///     transaction is dealt with.</item>
///   <item><b>Roll back and report</b> (<see cref="RollbackForConnectionAsync"/>) — deleting the connection,
///     removing the project. The user has already confirmed something that subsumes this, and asking a
///     second question about a connection they just deleted is noise. It still reaches the status line:
///     rolled back is a thing that happened, not a detail.</item>
/// </list>
/// </summary>
internal static class TransactionGuard
{
    /// <summary>Why <paramref name="tab"/> may not be re-pointed right now, or null when it may.</summary>
    public static string? ReasonToRefuse(TabTransactions transactions, EditorTabViewModel tab, string action)
        => transactions.For(tab) is { } open
            ? $"This tab has an uncommitted transaction on {open.ConnectionName}/{open.Database} — "
              + $"commit or roll it back before you {action}."
            : null;

    /// <summary>
    /// Ask about the transactions <paramref name="match"/> selects, and <b>do nothing else</b>. True means
    /// the user agreed — or that there was nothing to ask about.
    /// <para>
    /// Separate from <see cref="RollbackAsync"/> because asking and acting are not always adjacent. Where a
    /// second question follows (quit asks about running queries too) or a later step can still abandon the
    /// whole thing (a close whose save picker is dismissed), rolling back on the way past would destroy
    /// uncommitted work on an action the user then cancelled — and unlike a cancelled query, a rolled-back
    /// transaction cannot be re-run.
    /// </para>
    /// </summary>
    public static async Task<bool> AskAsync(
        TabTransactions transactions, IDialogService? dialogs, Func<TabTransaction, bool> match, string action)
    {
        var count = transactions.All.Count(match);
        if (count == 0) return true;
        // No dialog service at all (headless, a test harness) proceeds, for the reason the interface gives:
        // a deliberate rollback beats the connection being severed under it a moment later.
        return dialogs is null || await dialogs.ConfirmDiscardTransactionsAsync(count, action);
    }

    /// <summary>Roll back what <see cref="AskAsync"/> asked about, once the action is certain.</summary>
    public static Task<int> RollbackAsync(TabTransactions transactions, Func<TabTransaction, bool> match)
        => transactions.RollbackWhereAsync(match, CancellationToken.None);

    /// <summary>
    /// Ask, and roll back immediately if the user agrees — for the paths where nothing else can intervene
    /// between the question and the act (Disconnect, a metadata refresh). Where something can, use
    /// <see cref="AskAsync"/> and <see cref="RollbackAsync"/>.
    /// </summary>
    public static async Task<bool> ConfirmAsync(
        TabTransactions transactions, IDialogService? dialogs, Func<TabTransaction, bool> match, string action)
    {
        if (!await AskAsync(transactions, dialogs, match, action)) return false;
        await RollbackAsync(transactions, match);
        return true;
    }

    /// <summary>Ask before an action that ends every transaction on one connection.</summary>
    public static Task<bool> ConfirmForConnectionAsync(
        TabTransactions transactions, IDialogService? dialogs, Guid connectionId, string action)
        => ConfirmAsync(transactions, dialogs, t => t.Key.ConnectionId == connectionId, action);

    /// <summary>Ask about one tab's transaction without ending it — see <see cref="AskAsync"/>.</summary>
    public static Task<bool> AskForTabAsync(
        TabTransactions transactions, IDialogService? dialogs, EditorTabViewModel tab, string action)
        => transactions.For(tab) is { } open
            ? AskAsync(transactions, dialogs, t => ReferenceEquals(t, open), action)
            : Task.FromResult(true);

    /// <summary>Roll back one tab's transaction, once whatever asked about it is certain to happen.</summary>
    public static Task<int> RollbackForTabAsync(TabTransactions transactions, EditorTabViewModel tab)
        => transactions.For(tab) is { } open
            ? RollbackAsync(transactions, t => ReferenceEquals(t, open))
            : Task.FromResult(0);

    /// <summary>Roll back without asking, and say how many — for the paths the user has already confirmed
    /// something larger on. Returns the status line, or null when there was nothing open.</summary>
    public static async Task<string?> RollbackForConnectionAsync(TabTransactions transactions, Guid connectionId)
    {
        var rolled = await transactions.RollbackConnectionAsync(connectionId, CancellationToken.None);
        return rolled == 0
            ? null
            : rolled == 1
                ? "Rolled back 1 uncommitted transaction."
                : $"Rolled back {rolled} uncommitted transactions.";
    }
}
