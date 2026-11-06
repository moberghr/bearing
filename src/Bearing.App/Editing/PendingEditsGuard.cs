using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Results;
using Bearing.App.ViewModels;

namespace Bearing.App.Editing;

/// <summary>
/// Unsaved grid edits are never dropped without asking (spec §7). A run replaces the pane's results and the
/// zones inside what it ran; ✕ removes a zone; both would throw away whatever edits those results hold. This
/// works out what would go — pure, so it is testable without a window — and asks.
/// </summary>
public static class PendingEditsGuard
{
    /// <summary>What a replacement would lose: <see cref="Changes"/> unsaved changes spread over
    /// <see cref="Places"/> results (the pane counts as one, each zone as one).</summary>
    public sealed record Loss(int Changes, int Places, IReadOnlyList<ResultSetViewModel> Sets)
    {
        public bool IsEmpty => Changes == 0;

        public string Text => PendingEdits.Describe(Changes, Places);

        /// <summary>Revert the edits — the user chose to discard them and the replacement happened. Sets in
        /// <paramref name="keep"/> are left alone: something still shows them (a zone a refused or
        /// unplaceable run did not replace).</summary>
        public void Discard(IEnumerable<ResultSetViewModel>? keep = null)
        {
            var kept = keep is null ? new HashSet<ResultSetViewModel>() : new HashSet<ResultSetViewModel>(keep);
            foreach (var set in Sets.Where(s => s.HasPendingChanges && !kept.Contains(s)))
            {
                set.RevertPending();
            }
        }
    }

    /// <summary>The zones a run over <paramref name="scope"/> replaces (<see cref="RunScope.Replaces"/>, the rule
    /// <see cref="EditorTabViewModel.ApplyInlineRun"/> applies).</summary>
    public static IEnumerable<InlineResultZone> ZonesReplacedBy(EditorTabViewModel tab, RunScope scope)
        => tab.InlineZones.Where(z => scope.Replaces(z.Offset));

    /// <summary>
    /// What a run over <paramref name="scope"/> on <paramref name="tab"/> would lose: the pane's frames (a run
    /// replaces the tab's results and clears its Back stack) and the zones it replaces.
    /// <para>
    /// With zones shown, a pane set that a zone the run leaves in place also holds is not lost — the last run's
    /// sets are both the pane's and its zone's, and running another statement leaves that zone, edits and all,
    /// on screen. In Pane mode zones aren't shown, so nothing the pane drops counts as kept.
    /// </para>
    /// </summary>
    public static Loss ForRun(EditorTabViewModel tab, RunScope scope, bool zonesShown)
    {
        var replaced = ZonesReplacedBy(tab, scope).ToList();
        var survivors = zonesShown
            ? tab.InlineZones.Except(replaced).SelectMany(z => z.AllSets).ToHashSet()
            : new HashSet<ResultSetViewModel>();
        return Of(new[] { tab.AllSets.Where(s => !survivors.Contains(s)) }.Concat(replaced.Select(z => z.AllSets)));
    }

    /// <summary>What closing <paramref name="zone"/> would lose: its sets that nothing else on the tab — the pane
    /// or another zone — still holds.</summary>
    public static Loss ForZone(EditorTabViewModel tab, InlineResultZone zone)
    {
        var elsewhere = tab.AllSets.Concat(tab.InlineZones.Where(z => z != zone).SelectMany(z => z.AllSets)).ToHashSet();
        return Of([zone.AllSets.Where(s => !elsewhere.Contains(s))]);
    }

    /// <summary>What handing the pane a zone's frames (the zone's placement toggle) would lose: the pane's
    /// frames that no zone on the tab holds — the zone being moved included.</summary>
    public static Loss ForPaneTakeover(EditorTabViewModel tab)
    {
        var held = tab.InlineZones.SelectMany(z => z.AllSets).ToHashSet();
        return Of([tab.AllSets.Where(s => !held.Contains(s))]);
    }

    /// <summary>What closing <paramref name="tab"/> would lose: the pane's frames and every zone's.</summary>
    public static Loss ForTab(EditorTabViewModel tab)
        => Of(new[] { tab.AllSets }.Concat(tab.InlineZones.Select(z => z.AllSets)));

    private static Loss Of(IEnumerable<IEnumerable<ResultSetViewModel>> places)
    {
        var seen = new HashSet<ResultSetViewModel>();
        var count = 0;
        foreach (var place in places)
        {
            // A run's sets are both the tab's results and its zone's: a place only counts for sets not
            // already counted, so the same edits are never reported twice.
            var fresh = place.Where(s => s.HasPendingChanges && seen.Add(s)).ToList();
            if (fresh.Count > 0)
            {
                count++;
            }
        }
        return new Loss(seen.Sum(s => s.PendingCount), count, seen.ToList());
    }

    /// <summary>Ask before <paramref name="action"/> loses <paramref name="loss"/>; true means go ahead. Nothing
    /// to lose → true without asking. Reverting is the caller's, once the replacement has really happened
    /// (<see cref="Loss.Discard"/>): a run can still be refused or fail after this answer.</summary>
    public static async Task<bool> ConfirmAsync(Loss loss, string action,
        Func<string, string, Task<bool>> ask)
        => loss.IsEmpty || await ask(loss.Text, action);
}
