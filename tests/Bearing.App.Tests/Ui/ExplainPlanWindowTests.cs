using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bearing.App.Views;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The plan window's two claims that only a realized visual can hold: every node gets a row, and the node
/// worth looking at is the one marked. The arithmetic behind both is tested without a UI in the Sql suite.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ExplainPlanWindowTests
{
    private readonly UiTestSession _ui;

    public ExplainPlanWindowTests(UiTestSession ui) => _ui = ui;

    /// <summary>Four nodes; the deepest child holds the most self time, so it is not the root.</summary>
    private const string AnalyzedJson = """
        [{"Plan":{"Node Type":"Aggregate","Total Cost":812.4,"Plan Rows":1,"Actual Rows":1,
           "Actual Total Time":48.2,"Actual Loops":1,
          "Plans":[{"Node Type":"Hash Join","Total Cost":790.1,"Plan Rows":950,"Actual Rows":5462,
            "Actual Total Time":44.9,"Actual Loops":1,
            "Plans":[{"Node Type":"Seq Scan","Relation Name":"rental","Total Cost":310.0,
                      "Plan Rows":16044,"Actual Rows":16044,"Actual Total Time":9.1,"Actual Loops":1},
                     {"Node Type":"Index Scan","Relation Name":"film","Index Name":"film_pkey",
                      "Total Cost":8.3,"Plan Rows":95,"Actual Rows":1000,"Actual Total Time":22.4,
                      "Actual Loops":1}]}]},
          "Planning Time":0.42,"Execution Time":48.6}]
        """;

    private static ExplainPlanWindow Show(string json, bool analyzed, bool rolledBack)
    {
        var plan = ExplainPlanParser.Parse(json, analyzed, rolledBack)!;
        var window = new ExplainPlanWindow(plan, "select count(*) from rental r join film f on true");
        window.Show();
        for (var i = 0; i < 4; i++)
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        return window;
    }

    private static string[] Labels(Window window)
        => window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .Where(t => t.Length > 0)
            .ToArray();

    [Fact]
    public Task Every_node_of_the_plan_gets_a_row() => _ui.Run(() =>
    {
        var window = Show(AnalyzedJson, analyzed: true, rolledBack: true);

        var items = window.GetVisualDescendants().OfType<TreeViewItem>().ToList();
        Assert.Equal(4, items.Count);

        var labels = Labels(window);
        Assert.Contains("Aggregate", labels);
        Assert.Contains("Hash Join", labels);
        Assert.Contains("Seq Scan on rental", labels);
        Assert.Contains("Index Scan on film using film_pkey", labels);
        window.Close();
    });

    [Fact]
    public Task The_node_with_the_most_self_time_is_the_one_marked() => _ui.Run(() =>
    {
        // Deliberately not the root. Postgres reports inclusive time, so a window that marked the largest
        // total would always point at the root and never at the node to fix — the mistake this guards.
        var window = Show(AnalyzedJson, analyzed: true, rolledBack: true);

        var marked = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.FontWeight == FontWeight.SemiBold && (t.Text ?? "").Length > 0)
            .Select(t => t.Text)
            .ToList();

        Assert.Equal(["Index Scan on film using film_pkey"], marked);
        window.Close();
    });

    [Fact]
    public Task An_analysed_plan_says_the_statement_was_run_and_rolled_back() => _ui.Run(() =>
    {
        // ANALYZE executes the statement. Not saying so would be the feature's worst failure — worse than
        // not having it, because the user would not know to be careful.
        var window = Show(AnalyzedJson, analyzed: true, rolledBack: true);

        var text = string.Join(" ", Labels(window));
        Assert.Contains("running the statement", text);
        Assert.Contains("rolled back", text);
        window.Close();
    });

    [Fact]
    public Task A_plan_only_explain_makes_no_such_claim() => _ui.Run(() =>
    {
        // Nothing ran, so a warning about having run it would be a lie in the other direction.
        const string json = """
            [{"Plan":{"Node Type":"Seq Scan","Relation Name":"film","Total Cost":65.0,"Plan Rows":200}}]
            """;
        var window = Show(json, analyzed: false, rolledBack: false);

        var text = string.Join(" ", Labels(window));
        Assert.DoesNotContain("running the statement", text);
        Assert.DoesNotContain("rolled back", text);
        // …and it still shows the plan, with the estimate rather than actuals.
        Assert.Contains("Seq Scan on film", Labels(window));
        Assert.Contains(Labels(window), l => l.Contains("~200 rows"));
        window.Close();
    });

    [Fact]
    public Task A_badly_estimated_node_is_called_out() => _ui.Run(() =>
    {
        // 95 estimated against 1,000 actual on the index scan — 10.5x, which is the number that explains a
        // bad join order and is invisible unless something compares the two for you.
        var window = Show(AnalyzedJson, analyzed: true, rolledBack: true);

        Assert.Contains(Labels(window), l => l.Contains("10.5× out"));
        // Invariant decimal separator, so one row cannot read "22.4 ms self … estimate 10,5x out".
        Assert.DoesNotContain(Labels(window), l => l.Contains("10,5"));
        window.Close();
    });

    // ---- the bars ---------------------------------------------------------------------------------

    /// <summary>
    /// Each node gets a bar, and the busiest node's fills its track.
    /// <para>
    /// Scaled to the busiest node rather than to the query total: a plan whose time is spread evenly would
    /// otherwise render as a column of near-empty tracks, which is the case where the comparison is most
    /// wanted. So the widest bar is always the slowest node, whatever the absolute numbers are.
    /// </para>
    /// </summary>
    [Fact]
    public Task The_busiest_node_gets_the_widest_bar() => _ui.Run(() =>
    {
        var window = Show(AnalyzedJson, analyzed: true, rolledBack: true);

        // The filled child inside each track; the track itself is the fixed-width parent.
        var fills = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Child is Border)
            .Select(b => ((Border)b.Child!).Width)
            .Where(w => !double.IsNaN(w))
            .ToList();

        Assert.Equal(4, fills.Count);
        // 22.4 self is the largest, so one bar is full width and none exceeds it.
        var widest = fills.Max();
        Assert.Equal(110, widest, 1);
        Assert.All(fills, w => Assert.True(w <= widest + 0.001));
        window.Close();
    });

    [Fact]
    public Task A_nearly_free_node_still_gets_a_visible_sliver() => _ui.Run(() =>
    {
        // A node that took a measurable but tiny slice must not render as an empty track: "almost nothing"
        // and "nothing" are different findings, and rounding the first to zero hides it.
        const string json = """
            [{"Plan":{"Node Type":"Gather","Actual Total Time":100.0,"Actual Loops":1,
              "Plans":[{"Node Type":"Memoize","Actual Total Time":0.01,"Actual Loops":1}]}}]
            """;
        var window = Show(json, analyzed: true, rolledBack: true);

        var fills = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Child is Border)
            .Select(b => ((Border)b.Child!).Width)
            .Where(w => !double.IsNaN(w))
            .ToList();

        Assert.All(fills, w => Assert.True(w >= 1, $"a node rendered a {w}px bar"));
        window.Close();
    });

    [Fact]
    public Task A_plan_that_was_not_analysed_draws_no_bars() => _ui.Run(() =>
    {
        // There is no self time to scale, so a bar would be a chart of nothing.
        const string json = """
            [{"Plan":{"Node Type":"Seq Scan","Relation Name":"film","Total Cost":65.0,"Plan Rows":200}}]
            """;
        var window = Show(json, analyzed: false, rolledBack: false);

        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), b => b.Child is Border);
        window.Close();
    });
}
