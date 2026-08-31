using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Styling;

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
        var row = new Style(x => x.OfType<TreeViewItem>());
        // MinHeight is the setter that matters: Fluent's floor is what held the row open regardless of how
        // little was in it. The padding then decides the height, which is why it is stated here.
        row.Setters.Add(new Setter(Layoutable.MinHeightProperty, 0d));
        row.Setters.Add(new Setter(TemplatedControl.PaddingProperty, RowPadding));
        tree.Styles.Add(row);
    }
}
