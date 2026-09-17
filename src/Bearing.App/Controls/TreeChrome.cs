using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Bearing.App.Controls;

/// <summary>
/// Row metrics for the sidebar's navigator trees (#71). Both trees inherited Avalonia Fluent's stock
/// <see cref="TreeViewItem"/> metrics, which are sized as touch targets: a row holding one line of 13px text
/// and a 15×15 glyph stood far taller than its content, so an expanded table showed a handful of column
/// names spread down the panel and the tree read as sparse rather than scannable — the opposite of what a
/// navigator you scan is for.
/// <para>
/// Applied in code, per tree, for the same reason <see cref="ResultGridChrome"/> is: the numbers are stated
/// once and both trees are held to them, rather than each XAML style carrying its own copy to drift from.
/// The behavioural setters (the <c>IsExpanded</c> two-way binding, the environment wash) stay in the XAML —
/// they are per-tree bindings, not metrics.
/// </para>
/// </summary>
internal static class TreeChrome
{
    /// <summary>
    /// Selecting a row must not scroll the tree sideways.
    /// <para>
    /// <c>TreeView</c> calls <c>BringIntoView()</c> on the container whenever the selection changes, and the
    /// argument-less overload asks for the row's <b>whole width</b> — which on a navigator row is the full
    /// indent plus a long qualified name, wider than the panel. The scroll presenter honoured it by
    /// scrolling right, so clicking a group like <i>Views</i> threw the panel sideways to chase the end of a
    /// row the user had just successfully clicked.
    /// </para>
    /// <para>
    /// The request is rewritten to a <b>zero-width sliver at an x already inside the viewport</b>, which
    /// leaves it purely vertical: nothing moves horizontally, while a row below the fold still scrolls into
    /// view — what keyboard navigation and "go to table" (§9.11) need. The horizontal scrollbar keeps
    /// working; a long name is still readable by dragging it.
    /// </para>
    /// <para>
    /// Zeroing the <em>width</em> alone was the first attempt and was not enough. The rect keeps the row's
    /// own left edge as its x, and in a panel already scrolled right that edge is off-screen to the
    /// <em>left</em> — so the presenter still acted, by scrolling back to zero. Clamping the x into the
    /// visible range is what makes the request a no-op in both directions. Found by review; the first two
    /// tests both started at offset zero and could not see it.
    /// </para>
    /// <para>
    /// A class handler on <see cref="TreeViewItem"/>, because the routed event is bubble-only and the
    /// presenter that acts on it is an <em>ancestor</em> of the item — a handler on the TreeView itself runs
    /// after the scroll has already happened, and the item is the event's source.
    /// </para>
    /// <para>
    /// <b>Ours has to run after Avalonia's own.</b> <c>TreeViewItem</c> registers a class handler for this
    /// same event that rewrites <c>TargetRect</c> to the header's bounds — so whichever of the two registers
    /// second wins, and that was decided by which type's static initialiser ran first. It silently reversed
    /// between two runs of the suite. Forcing <c>TreeViewItem</c>'s initialiser to complete first makes the
    /// order ours to state rather than the loader's, and the header rect it produces is the better input
    /// anyway: it is the row as drawn, indent included.
    /// </para>
    /// <para>
    /// Two guards keep that reach from being a behaviour change for every tree in the app:
    /// </para>
    /// <list type="bullet">
    /// <item>the item's tree must be one <see cref="Apply"/> was called on, so a tree that never opted in
    /// behaves exactly as Avalonia ships it — otherwise "is the trim in force" would depend on whether
    /// anything had touched this type yet, which is the test-order trap §4.5 documents for
    /// <c>ThemeBrush.AtAlphaCached</c>;</item>
    /// <item>the request's target must be the row container <em>itself</em>. A descendant that asks to be
    /// revealed — an inline-rename text box scrolling to its caret — is asking for a horizontal reveal on
    /// its own behalf, and is none of this rule's business.</item>
    /// </list>
    static TreeChrome()
    {
        RuntimeHelpers.RunClassConstructor(typeof(TreeViewItem).TypeHandle);
        Control.RequestBringIntoViewEvent.AddClassHandler<TreeViewItem>(
            KeepSelectionVertical, RoutingStrategies.Bubble);
    }

    /// <summary>The trees <see cref="Apply"/> has been called on. Weak, so a tree that goes away takes its
    /// entry with it — the same shape <c>GridSelectionController</c> uses to cache a grid's corner.</summary>
    private static readonly ConditionalWeakTable<TreeView, object> Managed = new();

    private static void KeepSelectionVertical(TreeViewItem item, RequestBringIntoViewEventArgs e)
    {
        if (!ReferenceEquals(e.TargetObject, item)) return;                       // a descendant's own ask
        if (item.FindAncestorOfType<TreeView>() is not { } tree) return;
        if (!Managed.TryGetValue(tree, out _)) return;                            // not one of ours

        var rect = e.TargetRect;
        // Where the viewport's own left edge falls in the item's coordinates. Translating through the
        // presenter is what accounts for the current scroll, which is the whole point.
        if (item.FindAncestorOfType<ScrollContentPresenter>() is { } presenter
            && presenter.TranslatePoint(default, item) is { } viewport)
        {
            var right = viewport.X + presenter.Bounds.Width;
            // Already-visible x → unchanged, so the request is satisfied where it stands. Off to either
            // side → pulled to the nearest visible edge, which is also satisfied where it stands.
            rect = rect.WithX(Math.Clamp(rect.X, viewport.X, Math.Max(viewport.X, right - 1)));
        }
        e.TargetRect = rect.WithWidth(0);
    }

    /// <summary>Vertical padding inside a row. Two pixels either side of a 15px glyph gives the ~19px row the
    /// content actually asks for, and leaves the expander chevron a target you can still hit.</summary>
    public static readonly Thickness RowPadding = new(4, 2);

    /// <summary>How tall a row may be before it is wasting the panel. Not enforced at runtime — it is the
    /// number the test measures against, kept here so the intent and the check cannot drift.</summary>
    public const double RowHeightCeiling = 24;

    /// <summary>Tighten one tree's rows. Safe to call before the tree has any items.
    /// <para>
    /// The per-level indent is deliberately left alone: it comes out of Fluent's <c>TreeViewItem</c>
    /// template rather than off a property, so changing it means retemplating, and a five-deep schema tree
    /// in a narrow panel is a horizontal problem worth its own change rather than a rider on this one.
    /// </para></summary>
    public static void Apply(TreeView tree)
    {
        Managed.AddOrUpdate(tree, tree);   // opts this tree into KeepSelectionVertical

        var row = new Style(x => x.OfType<TreeViewItem>());
        // MinHeight is the setter that matters: Fluent's floor is what held the row open regardless of how
        // little was in it. The padding then decides the height, which is why it is stated here.
        row.Setters.Add(new Setter(Layoutable.MinHeightProperty, 0d));
        row.Setters.Add(new Setter(TemplatedControl.PaddingProperty, RowPadding));
        tree.Styles.Add(row);
    }
}
