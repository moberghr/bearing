using System.Threading.Tasks;
using Squirrel.App.Controls;
using Squirrel.App.ViewModels;

namespace Squirrel.App.Views;

public partial class MainWindow
{
    // The floating pending-changes script panel (design RESULTS_GRID §5) lives in its own control
    // (Controls/PendingChangesOverlay); this partial just bridges it to the result set's preview/discard/
    // save. Constructed in the ctor (needs `this` as the overlay-layer owner). Wired to ResultsView.PreviewSql.
    private readonly PendingChangesOverlay _pendingPanel;

    /// <summary>Open the floating pending-changes panel for a result set's write statements (nothing pending
    /// → no-op). Discard reverts + re-renders; Run &amp; save commits + refreshes the row tints.</summary>
    private void ShowPendingScript(ResultSetViewModel rs)
    {
        if (Vm is null) return;
        var statements = Vm.Execution.PreviewChangeStatements(rs);
        if (statements.Count == 0) return;
        _pendingPanel.Show(
            statements,
            onDiscard: async () => { if (Vm is not null) { await Vm.Execution.DiscardChangesAsync(rs); RebuildResults(Vm.Workspace.SelectedTab); } },
            onSave: async () => { if (Vm is not null) { await Vm.Execution.SaveChangesAsync(rs); ResultsView.RefreshRowHighlights(); } });
    }
}
