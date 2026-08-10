using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

/// <summary>
/// The closing half of background execution. Queries survive tab switches and project switches, so quitting
/// is the one action that can silently throw one away — this asks first, then cancels every in-flight run.
/// Kept off <c>MainWindow</c> (§9.1); the window keeps only its <c>OnClosing</c> override, which has to be
/// there because it is a virtual.
/// </summary>
internal static class QuitGuard
{
    /// <summary>How many tabs have a query in flight right now — across <b>every</b> open project, since a
    /// project switch parks its tabs rather than closing them and a run there is just as easy to lose.</summary>
    public static int RunningCount(ShellViewModel? vm)
        => vm?.Workspace.AllTabs.Count(t => t.IsRunning) ?? 0;

    /// <summary>Ask about the in-flight runs and, if the user agrees, cancel them all. True means the close
    /// may now proceed. Cancellation is fire-and-forget: the shutdown path force-disposes the sessions
    /// anyway, so a query that ignores its token can't wedge quit.</summary>
    public static async Task<bool> ConfirmAsync(ShellViewModel vm, IDialogService dialogs, int running)
    {
        if (!await dialogs.ConfirmCancelRunningAsync(running)) return false;
        foreach (var tab in vm.Workspace.AllTabs) tab.CancelRun();
        return true;
    }
}
