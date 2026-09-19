using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

/// <summary>
/// The closing half of background execution, and of manual-commit mode. Queries survive tab switches and
/// project switches, and so does an uncommitted transaction, so quitting is the one action that can silently
/// throw either away — this asks first, then cancels every in-flight run and rolls back every open
/// transaction. Kept off <c>MainWindow</c> (§9.1); the window keeps only its <c>OnClosing</c> override,
/// which has to be there because it is a virtual.
/// </summary>
internal static class QuitGuard
{
    /// <summary>How many tabs have a query in flight right now — across <b>every</b> open project, since a
    /// project switch parks its tabs rather than closing them and a run there is just as easy to lose.</summary>
    public static int RunningCount(ShellViewModel? vm)
        => vm?.Workspace.AllTabs.Count(t => t.IsRunning) ?? 0;

    /// <summary>
    /// How many uncommitted transactions are open (#131). Also across every project: sessions and their
    /// pools outlive a project switch, so a transaction opened in a project that is no longer on screen is
    /// still on a connection this process is about to sever.
    /// </summary>
    public static int OpenTransactionCount(ShellViewModel? vm)
        => vm?.Context.Transactions.Count ?? 0;

    /// <summary>Whether the close needs to stop and ask about anything at all.</summary>
    public static bool NeedsConfirmation(ShellViewModel? vm)
        => RunningCount(vm) > 0 || OpenTransactionCount(vm) > 0;

    /// <summary>
    /// Ask about the in-flight runs and the open transactions and, if the user agrees to both, cancel the
    /// runs and roll the transactions back. True means the close may now proceed.
    /// <para>
    /// Two prompts rather than one combined: they are different losses with different answers, and a single
    /// "you will lose some things" would let a user agreeing to abandon a query also silently agree to
    /// discard a transaction. Transactions are asked about <b>first</b> — a rolled-back transaction is the
    /// one of the two that cannot be got back, so a user who keeps it should not have had to answer about
    /// query results on the way.
    /// </para>
    /// <para>
    /// Cancellation of the runs stays fire-and-forget: the shutdown path force-disposes the sessions anyway,
    /// so a query that ignores its token can't wedge quit. The rollback is <b>not</b> fire-and-forget — it
    /// is awaited, because a severed connection rolls back on the server's schedule rather than ours, and
    /// the whole point is that quitting ends the transaction deliberately.
    /// </para>
    /// </summary>
    public static async Task<bool> ConfirmAsync(ShellViewModel vm, IDialogService dialogs, int running)
    {
        // Both questions first, then both actions. Rolling back as soon as the first was answered meant a
        // user who then declined the second kept their queries, kept the app open — and had already lost
        // the transactions, which is the opposite of what asking about them first was for.
        if (!await TransactionGuard.AskAsync(vm.Context.Transactions, dialogs, _ => true, "quit"))
            return false;
        if (running > 0 && !await dialogs.ConfirmCancelRunningAsync(running)) return false;

        await TransactionGuard.RollbackAsync(vm.Context.Transactions, _ => true);
        if (running > 0) foreach (var tab in vm.Workspace.AllTabs) tab.CancelRun();
        return true;
    }
}
