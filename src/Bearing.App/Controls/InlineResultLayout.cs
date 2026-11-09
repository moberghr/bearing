using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;

namespace Bearing.App.Controls;

/// <summary>
/// What a <see cref="ResultView"/> does differently as an inline zone (spec docs/specs/2026-11-06-inline-results.md
/// §5–§6, §10), kept out of the view's composition root (§9.1): the zone's one-bar layout, each grid's
/// fit-up-to-the-cap height, and when a Stacked / Tabs change is worth a rebuild. One per view.
/// </summary>
internal sealed class InlineResultLayout
{
    // Grids sized to their rows, and the row-count subscriptions that re-size them; both dropped on rebuild.
    private readonly List<(DataGrid Grid, ResultSetViewModel Result)> _sized = new();
    private readonly List<(INotifyCollectionChanged Rows, NotifyCollectionChangedEventHandler Handler)> _subscriptions = new();
    private double? _cap;
    private ResultsViewMode? _builtMode;   // the Stacked / Tabs mode the current content was laid out in

    /// <summary>The zone's caption ("3 result sets from this batch"), shown in its top bar.</summary>
    public string? Caption { get; set; }

    /// <summary>A warning shown in the zone's top bar instead (the statement it hung under was removed).</summary>
    public string? Notice { get; set; }

    /// <summary>The zone's placement toggle — show these results in the pane instead.</summary>
    public Action? ShowInPaneRequested { get; set; }

    /// <summary>Set a view's Stacked / Tabs <paramref name="field"/>, rebuilding only when the layout depends on
    /// it now (<see cref="RebuildsOnModeChange"/>).</summary>
    public void SetViewMode(ref ResultsViewMode field, ResultsViewMode value, Visual view, bool inline,
        IReadOnlyList<ResultSetViewModel>? results, Action rebuild)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        if (RebuildsOnModeChange(view, inline, results))
        {
            rebuild();
        }
    }

    /// <summary>What a zone's header needs from its view.</summary>
    /// <param name="GoBack">Back (FK navigation), or null when there is nowhere to go back to.</param>
    /// <param name="ViewMode">The Stacked / Tabs setting in force.</param>
    /// <param name="SetViewMode">The user flipped Stacked / Tabs in this zone.</param>
    /// <param name="ShowInPane">The placement toggle, or null to hide it.</param>
    /// <param name="Close">✕, or null to hide it.</param>
    /// <param name="Caption">"3 result sets from this batch", or null.</param>
    /// <param name="Notice">A warning in place of the caption ("statement removed"), or null.</param>
    public sealed record Header(Action? GoBack, ResultsViewMode ViewMode, Action<ResultsViewMode> SetViewMode,
        Action? ShowInPane, Action? Close, string? Caption, string? Notice);

    /// <summary>Builds one set's container: (set, label, collapsible, trailing actions for its meta row).</summary>
    public delegate Control SetContainer(ResultSetViewModel set, string label, bool collapsible, Control? trailing);

    /// <summary>The cap on each grid's rows, in pixels (<see cref="InlineGridSizing"/>). Assigning re-sizes the
    /// grids in place (the grip drag) — no rebuild, so scroll and selection stay.</summary>
    public double Cap
    {
        get => _cap ?? InlineGridSizing.DefaultCap(ResultGridChrome.RowMinHeight);
        set
        {
            _cap = value;
            foreach (var (grid, result) in _sized)
            {
                Size(grid, result);
            }
        }
    }

    /// <summary>Give <paramref name="grid"/> a definite height that follows the loaded rows up to the cap
    /// (spec §6) — the editor measures a zone with infinite height, and a grid measured that way would realize
    /// every row.</summary>
    public void Track(DataGrid grid, ResultSetViewModel result, bool inline)
    {
        if (!inline)
        {
            return;   // the pane's grids share its height
        }
        _sized.Add((grid, result));
        Size(grid, result);
        void OnRows(object? sender, NotifyCollectionChangedEventArgs e) => Size(grid, result);
        result.Rows.CollectionChanged += OnRows;
        _subscriptions.Add((result.Rows, OnRows));
    }

    /// <summary>The view is rebuilding, in <paramref name="mode"/>: let go of the grids it is about to discard.</summary>
    public void Clear(ResultsViewMode mode)
    {
        _builtMode = mode;
        _sized.Clear();
        foreach (var (rows, handler) in _subscriptions)
        {
            rows.CollectionChanged -= handler;
        }
        _subscriptions.Clear();
    }

    /// <summary>
    /// Whether a Stacked / Tabs change should rebuild <paramref name="view"/> now: always for the pane
    /// (<paramref name="inline"/> false). A zone with one set lays out
    /// the same either way, and one that is not on screen (another tab's, or scrolled away) is rebuilt when it
    /// is next attached, and only if the mode then differs from the one it was laid out in (<see cref="IsStale"/>)
    /// — a rebuild drops scroll, selection and the inspector, and a zone comes back as it was left (spec §10).
    /// </summary>
    public bool RebuildsOnModeChange(Visual view, bool inline, IReadOnlyList<ResultSetViewModel>? results)
        => !inline || (results is { Count: > 1 } && view.IsAttachedToVisualTree());

    /// <summary>
    /// Rebuild <paramref name="view"/> when it comes back on screen laid out in a Stacked / Tabs mode it has since
    /// left (<see cref="IsStale"/>). Posted: a zone is attached inside the editor's measure pass, and its content
    /// must not be swapped mid-layout.
    /// </summary>
    public void Watch(Visual view, Func<bool> inline, Func<ResultsViewMode> mode,
        Func<IReadOnlyList<ResultSetViewModel>?> results, Action rebuild)
        => view.AttachedToVisualTree += (_, _) =>
        {
            if (inline() && IsStale(mode(), results()))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (IsStale(mode(), results()))
                    {
                        rebuild();
                    }
                });
            }
        };

    /// <summary>Whether the content was laid out in a different Stacked / Tabs mode than <paramref name="mode"/>
    /// and the layout depends on it (several sets) — a change made while the view was off screen. A mode flipped
    /// and flipped back is not stale.</summary>
    public bool IsStale(ResultsViewMode mode, IReadOnlyList<ResultSetViewModel>? results)
        => results is { Count: > 1 } && _builtMode is { } built && built != mode;

    /// <summary>
    /// An inline zone's layout (spec §5): one header bar — merged into the set's own meta row when there is a
    /// single set and nothing to say above it — then the sets one under another (or as tabs). No splitter: the
    /// editor gives a zone unlimited height, so each grid sizes itself (<see cref="Cap"/>).
    /// </summary>
    /// <param name="tabs">The tabbed body (the pane's own, which sizes per tab).</param>
    public static Control Build(IReadOnlyList<ResultSetViewModel> results, Header header, SetContainer container,
        Func<Control> tabs)
    {
        var actions = ResultChrome.ZoneActions(header.GoBack, results.Count > 1 ? header.ViewMode : null,
            header.SetViewMode, header.ShowInPane, header.Close);

        var root = new DockPanel { LastChildFill = true };
        var merged = results.Count == 1 && header.Caption is null && header.Notice is null;
        if (!merged)
        {
            var bar = ResultChrome.ZoneHeader(header.Notice ?? header.Caption, warn: header.Notice is not null, actions);
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
        }

        if (results.Count == 1)
        {
            root.Children.Add(container(results[0], "Result", collapsible: false, trailing: merged ? actions : null));
        }
        else if (header.ViewMode == ResultsViewMode.Tabbed)
        {
            root.Children.Add(tabs());
        }
        else
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            for (var i = 0; i < results.Count; i++)
            {
                stack.Children.Add(container(results[i], $"Result {i + 1}", collapsible: true, trailing: null));
            }
            root.Children.Add(stack);
        }
        return root;
    }

    /// <summary>A meta row's right-hand side with the zone's actions after it, so a single-set zone has one
    /// header bar, not two.</summary>
    public static Control? WithTrailing(Control? right, Control? trailing)
    {
        if (trailing is null)
        {
            return right;
        }
        var both = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (right is not null)
        {
            both.Children.Add(right);
        }
        both.Children.Add(trailing);
        return both;
    }

    private void Size(DataGrid grid, ResultSetViewModel result)
        => grid.Height = InlineGridSizing.GridHeight(result.Rows.Count, ResultGridChrome.RowMinHeight, Cap);
}
