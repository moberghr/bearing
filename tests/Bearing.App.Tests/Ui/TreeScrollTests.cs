using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Selecting a row in a navigator tree must not scroll the panel sideways.
/// <para>
/// <c>TreeView</c> calls <c>BringIntoView()</c> on the container on every selection change, and that
/// overload asks for the container's <b>whole width</b>. A schema row is a deep indent plus a long
/// qualified name, so the rect is wider than the panel and the scroll presenter honoured it by scrolling
/// right — clicking a group row like <i>Views</i> threw the tree sideways to chase the end of a row the
/// user had just successfully clicked.
/// </para>
/// <para>
/// <see cref="TreeChrome"/> trims the requested rect to zero width, which leaves the request purely
/// vertical. Both halves are asserted here: no horizontal movement on a click, and a row below the fold
/// still scrolls into view — that second one is what "go to table" / F12 (§9.11) relies on, and a fix that
/// killed bring-into-view outright would have broken it silently.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class TreeScrollTests
{
    private readonly UiTestSession _ui;

    public TreeScrollTests(UiTestSession ui) => _ui = ui;

    /// <summary>Clicking a row in a tree that overflows its panel horizontally moves nothing sideways.</summary>
    [Fact]
    public Task Selecting_a_row_does_not_scroll_the_tree_sideways() => _ui.Run(() =>
    {
        var (window, tree) = Tree();
        var scroller = Scroller(tree);
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width,
            $"fixture must overflow horizontally: extent {scroller.Extent} viewport {scroller.Viewport}");
        Assert.Equal(0, scroller.Offset.X);

        var row = Rows(tree).Last();
        Assert.True(row.Bounds.Width > scroller.Viewport.Width,
            $"the row must be wider than the panel, got {row.Bounds.Width} vs {scroller.Viewport.Width}");
        Click(window, row);

        Assert.Equal(0, scroller.Offset.X);
        Assert.NotNull(tree.SelectedItem);   // …and the click still selected the row
        window.Close();
    });

    /// <summary>
    /// The same, from a viewport that is <b>already scrolled right</b> — which the two tests above were not,
    /// and which is why the first version of this fix passed them while still moving the panel.
    /// <para>
    /// Zeroing the requested rect's width is not enough on its own: the rect keeps the row's own left edge as
    /// its X, and once the panel is scrolled right that edge is off-screen to the <i>left</i>, so the
    /// presenter still honours the request — by scrolling back to zero. The request has to be a zero-width
    /// sliver at an X that is already inside the viewport.
    /// </para></summary>
    [Fact]
    public Task Selecting_a_row_in_an_already_scrolled_tree_does_not_move_it_either() => _ui.Run(() =>
    {
        var (window, tree) = Tree();
        var scroller = Scroller(tree);

        scroller.Offset = new Vector(120, 0);
        window.UpdateLayout();
        var before = scroller.Offset.X;
        Assert.True(before > 0, "the fixture must start scrolled right");

        // The row's own left edge is off-screen now, so aim through the middle of the *viewport* — the
        // fixture's first attempt clicked at the row's left edge, missed the panel entirely, and passed
        // while hitting nothing.
        var row = Rows(tree).Last();
        var at = scroller.TranslatePoint(new Point(scroller.Bounds.Width / 2, 0), window)!.Value;
        var y = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y;
        window.MouseMove(new Point(at.X, y));
        window.MouseDown(new Point(at.X, y), MouseButton.Left);
        window.MouseUp(new Point(at.X, y), MouseButton.Left);
        window.UpdateLayout();

        Assert.Equal(before, scroller.Offset.X);
        Assert.NotNull(tree.SelectedItem);
        window.Close();
    });

    /// <summary>The vertical half survives: a row below the fold still scrolls into view, which is what a
    /// programmatic reveal needs.</summary>
    [Fact]
    public Task A_row_below_the_fold_still_scrolls_into_view() => _ui.Run(() =>
    {
        var (window, tree) = Tree(rows: 60, height: 160);
        var scroller = Scroller(tree);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, "fixture must overflow vertically");
        Assert.Equal(0, scroller.Offset.Y);

        var last = tree.GetVisualDescendants().OfType<TreeViewItem>().Last();
        last.BringIntoView();
        window.UpdateLayout();

        Assert.True(scroller.Offset.Y > 0, "a row below the fold did not scroll into view");
        Assert.Equal(0, scroller.Offset.X);   // and it got there without going sideways
        window.Close();
    });

    private static void Click(Window window, Visual row)
    {
        // Near the row's left edge, where its label is — the part a user aims at.
        var at = row.TranslatePoint(new Point(8, row.Bounds.Height / 2), window)
                 ?? throw new InvalidOperationException("row is not connected to the window");
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.UpdateLayout();
    }

    private static ScrollViewer Scroller(TreeView tree)
        => tree.GetVisualDescendants().OfType<ScrollViewer>().First();

    /// <summary>The header of each realized row. A TreeViewItem's own bounds span its whole expanded
    /// subtree, so those answer a different question (same reason <c>TreeChromeTests</c> does this).</summary>
    private static IEnumerable<Control> Rows(TreeView tree)
        => tree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .Select(i => i.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.Name == "PART_HeaderPresenter"))
            .OfType<Control>()
            .Where(c => c.IsVisible && c.Bounds.Height > 0);

    /// <summary>A tree shaped like the schema navigator in a narrow panel: nested rows whose names are
    /// longer than the panel is wide, so the content overflows in both directions.</summary>
    private static (Window Window, TreeView Tree) Tree(int rows = 2, double height = 400)
    {
        var leaves = Enumerable.Range(1, rows)
            .Select(i => Node($"a-table-with-a-really-long-name-{i}"))
            .ToArray();
        var tree = new TreeView
        {
            ItemsSource = new[]
            {
                Node("a-server-with-a-long-name",
                    Node("a-database-with-a-long-name",
                        Node("public-schema-with-a-long-name", leaves))),
            },
            ItemTemplate = new FuncTreeDataTemplate<TreeNode>(
                (n, _) => new TextBlock { Text = n?.Name ?? "", FontSize = 13 },
                n => n.Children),
        };
        TreeChrome.Apply(tree);

        var window = new Window { Width = 200, Height = height, Content = tree };
        window.Show();
        // Each pass realizes one more level, so the whole fixture is expanded after four.
        for (var i = 0; i < 5; i++)
        {
            window.UpdateLayout();
            foreach (var item in tree.GetVisualDescendants().OfType<TreeViewItem>()) item.IsExpanded = true;
            window.UpdateLayout();
        }
        return (window, tree);
    }

    private static TreeNode Node(string name, params TreeNode[] children) => new(name, children);

    private sealed record TreeNode(string Name, IReadOnlyList<TreeNode> Children);
}
