using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.Core.Explain;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// A query plan, as a tree.
/// <para>
/// Its own window rather than a third mode of the results pane: a plan is read alongside the query that
/// produced it, not instead of the rows, and §0.4/§9.1 rule out growing <c>ResultView</c> for it. The
/// existing dialogs (settings, shortcuts, release notes) are the precedent.
/// </para>
/// <para>
/// The layout answers the two questions someone opens a plan with — <i>where did the time go</i> and
/// <i>what did the planner get wrong</i> — so self time and the estimate error sit on every row rather than
/// being buried in a detail pane. Everything is built from <see cref="ExplainPlan"/>, which is parsed and
/// tested without a UI.
/// </para>
/// </summary>
public sealed class ExplainPlanWindow : Window
{
    /// <summary>Above this factor, a row estimate is wrong enough to be the likely cause of a bad plan.</summary>
    private const double BadEstimate = 10;

    public ExplainPlanWindow(ExplainPlan plan, string statement)
    {
        Title = plan.Analyzed ? "Query plan (analysed) — Bearing" : "Query plan — Bearing";
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("Bg.Window");

        var root = new DockPanel { LastChildFill = true, Margin = new Avalonia.Thickness(14) };

        var header = Header(plan, statement);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        var scroll = new ScrollViewer { Content = Tree(plan) };
        // Own column, not an overlay — a plan's rows run to the right edge (§0.5.2's scrollbar fix).
        ScrollViewer.SetAllowAutoHide(scroll, false);
        root.Children.Add(scroll);

        Content = root;
    }

    /// <summary>Totals, and the warning that the statement was actually executed.</summary>
    private static Control Header(ExplainPlan plan, string statement)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 0, 0, 10) };

        panel.Children.Add(new TextBlock
        {
            Text = OneLine(statement),
            Foreground = Res("Text.Code"),
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = Metric("Font.Small"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var totals = new List<string>();
        if (plan.PlanningMs is { } planning) totals.Add($"planning {Ms(planning)}");
        if (plan.ExecutionMs is { } execution) totals.Add($"execution {Ms(execution)}");
        totals.Add($"{plan.Root.Flatten().Count()} nodes");
        panel.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", totals),
            Foreground = Res("Text.Muted"),
            FontSize = Metric("Font.Small"),
        });

        if (plan.Analyzed)
        {
            // Said plainly, because ANALYZE runs the statement. The rollback is why that is safe, and it is
            // not a promise that nothing happened at all — a sequence does not un-advance.
            panel.Children.Add(new TextBlock
            {
                Text = plan.RolledBack
                    ? "Measured by running the statement inside a transaction that was rolled back. "
                    + "Side effects outside the database — a consumed sequence, anything a trigger sent "
                    + "elsewhere — are not undone by that."
                    : "Measured by running the statement. It was not rolled back.",
                Foreground = Res("Warn.Amber"),
                FontSize = Metric("Font.Small"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return panel;
    }

    /// <summary>The plan as an expanded tree, in Postgres' own child order.</summary>
    private static Control Tree(ExplainPlan plan)
    {
        var worst = plan.Analyzed ? plan.Hotspots().FirstOrDefault() : null;

        // Every bar is drawn against the busiest node rather than against the query total, so the slowest
        // node fills its track and the rest are read relative to it. Scaling to the total instead makes a
        // plan whose time is spread evenly look uniformly idle, which is the case where the comparison is
        // most needed.
        var scale = plan.Root.Flatten().Max(n => n.SelfMs ?? 0);

        var tree = new TreeView
        {
            ItemsSource = new[] { Item(plan.Root, worst, scale) },
            Background = Brushes.Transparent,
        };
        return tree;
    }

    private static TreeViewItem Item(ExplainNode node, ExplainNode? worst, double scale)
        => new()
        {
            Header = Row(node, ReferenceEquals(node, worst), scale),
            IsExpanded = true,
            ItemsSource = node.Children.Select(c => Item(c, worst, scale)).ToList(),
        };

    /// <summary>
    /// A bar as wide as this node's share of the busiest node's self time.
    /// <para>
    /// The graph, and deliberately inline rather than a diagram beside the tree: the question a plan is
    /// opened with is "which of these took the time", and a bar on the row answers it without the reader
    /// converting milliseconds into proportions in their head. A node-link diagram looks more like a plan
    /// and says less about it.
    /// </para>
    /// <para>
    /// A floor of one pixel on any non-zero share, because a node that took a measurable but tiny slice
    /// should read as "almost nothing" rather than as nothing at all — an empty track and a 0.2% track mean
    /// different things.
    /// </para>
    /// </summary>
    private static Control Bar(double? selfMs, double scale, bool isWorst)
    {
        const double Track = 110;
        var track = new Border
        {
            Width = Track,
            Height = 6,
            CornerRadius = new Avalonia.CornerRadius(3),
            Background = Res("Bg.Hover"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (selfMs is not { } self || scale <= 0) return track;

        var share = self / scale;
        var width = share <= 0 ? 0 : Math.Max(1, share * Track);
        track.Child = new Border
        {
            Width = width,
            Height = 6,
            CornerRadius = new Avalonia.CornerRadius(3),
            // The busiest node in the plan's own colour, so the bar and the label agree about which row is
            // the problem; everything else in the quieter accent.
            Background = isWorst ? Res("Warn.Amber") : Res("Accent.Brand"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(track, $"{Ms(self)} of {Ms(scale)} in the busiest node ({share * 100:0.#}%)");
        return track;
    }

    /// <summary>One node's line: what it did, how long it kept to itself, and how wrong the estimate was.</summary>
    private static Control Row(ExplainNode node, bool isWorst, double scale)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        // The bar first, so the eye runs down a column of proportions rather than hunting for numbers at
        // varying indents — the tree's own indentation already carries the structure.
        if (scale > 0) line.Children.Add(Bar(node.SelfMs, scale, isWorst));

        line.Children.Add(new TextBlock
        {
            Text = node.Title,
            Foreground = isWorst ? Res("Warn.Amber") : Res("Text.Primary"),
            FontWeight = isWorst ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Self time, not total: Postgres reports inclusive figures, so a tree of totals just restates the
        // query's duration at every level and the root always looks worst.
        if (node.SelfMs is { } self)
            line.Children.Add(Chip($"{Ms(self)} self", Res("Text.Muted")));

        if (node.ActualRows is { } actual)
        {
            var loops = node.Loops ?? 1;
            var total = actual * loops;
            var text = loops > 1
                ? $"{Count(total)} rows ({Count(actual)} × {Count(loops)} loops)"
                : $"{Count(total)} rows";
            line.Children.Add(Chip(text, Res("Text.Muted")));
        }
        else if (node.EstimatedRows is { } estimated)
        {
            line.Children.Add(Chip($"~{Count(estimated)} rows", Res("Text.Faint")));
        }

        // The estimate error is the single most useful number in a plan: a stale estimate is what produces
        // the wrong join order, and it is invisible unless the two figures are compared for you.
        if (node.EstimateErrorFactor is { } factor && factor >= 2)
            line.Children.Add(Chip(
                // Invariant, like Ms and Count below. Without it this one chip picked up the machine's
                // separator and a row read "13.4 ms self … estimate 5,7× out" — two decimal conventions in
                // the same line, which reads as a rendering bug whichever one you prefer.
                $"estimate {factor.ToString("0.#", CultureInfo.InvariantCulture)}× out",
                factor >= BadEstimate ? Res("Error.Red") : Res("Warn.Amber")));

        if (node.SharedBlocksRead is { } blocks && blocks > 0)
            line.Children.Add(Chip($"{Count(blocks)} blocks read", Res("Text.Faint")));

        if (node.Filter is { Length: > 0 } filter)
            line.Children.Add(Chip(OneLine(filter), Res("Syntax.Keyword")));

        return line;
    }

    private static Control Chip(string text, IBrush foreground) => new TextBlock
    {
        Text = text,
        Foreground = foreground,
        FontSize = Metric("Font.Small"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Milliseconds at a readable precision — sub-millisecond nodes are common and "0 ms" hides them.</summary>
    private static string Ms(double value) => value switch
    {
        < 1 => $"{value.ToString("0.###", CultureInfo.InvariantCulture)} ms",
        < 1000 => $"{value.ToString("0.#", CultureInfo.InvariantCulture)} ms",
        _ => $"{(value / 1000).ToString("0.##", CultureInfo.InvariantCulture)} s",
    };

    private static string Count(double value) => value >= 1000
        ? value.ToString("#,##0", CultureInfo.InvariantCulture)
        : value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Collapse whitespace, so a multi-line statement or filter reads on one row.</summary>
    private static string OneLine(string text)
        => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
