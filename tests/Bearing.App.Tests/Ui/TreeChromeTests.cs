using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Row height in the sidebar's navigator trees (#71). Both trees inherited Fluent's stock TreeViewItem
/// metrics, which are touch targets — a row holding one line of text and a 15px glyph stood far taller than
/// its content, so an expanded table showed a handful of column names spread down the panel.
/// <para>
/// Measured on realized rows, since the claim is about layout and the numbers come out of a control theme
/// rather than out of anything a unit test could reach.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class TreeChromeTests
{
    private readonly UiTestSession _ui;

    public TreeChromeTests(UiTestSession ui) => _ui = ui;

    /// <summary>Rows sit no further apart than the pitch the chrome states.</summary>
    [Fact]
    public Task Tightened_rows_sit_within_the_stated_pitch() => _ui.Run(() =>
    {
        var (window, tree) = Tree(tightened: true);

        var pitch = RowPitch(tree);
        Assert.True(pitch <= TreeChrome.RowHeightCeiling,
            $"rows sit {pitch:0.##}px apart, over the {TreeChrome.RowHeightCeiling}px ceiling");
        window.Close();
    });

    /// <summary>…and that is the chrome's doing, not the content being short anyway. The same rows stand
    /// further apart on Fluent's stock metrics, which is the bug.</summary>
    [Fact]
    public Task The_chrome_is_what_closes_the_gap() => _ui.Run(() =>
    {
        var (tightWindow, tight) = Tree(tightened: true);
        var (stockWindow, stock) = Tree(tightened: false);

        var tightened = RowPitch(tight);
        var fluent = RowPitch(stock);

        Assert.True(tightened < fluent,
            $"tightened rows sit {tightened:0.##}px apart and stock rows {fluent:0.##}px — no saving");
        tightWindow.Close();
        stockWindow.Close();
    });

    /// <summary>Enough rows to be measuring the nested ones too — a column under a table is where the
    /// sparseness showed, and it inherits the same style.</summary>
    [Fact]
    public Task Every_realized_row_is_tightened() => _ui.Run(() =>
    {
        var (window, tree) = Tree(tightened: true);

        Assert.True(Rows(tree).Count() > 2, "the fixture must expand its child rows");
        Assert.True(RowPitch(tree) <= TreeChrome.RowHeightCeiling);
        window.Close();
    });

    /// <summary>Average vertical distance between one row and the next — what "objects per screen" means,
    /// and the only figure that captures the padding, the min-height and the content together.</summary>
    private static double RowPitch(TreeView tree)
    {
        var tops = Rows(tree)
            .Select(r => r.TranslatePoint(default, tree)?.Y ?? 0)
            .OrderBy(y => y)
            .ToList();
        Assert.True(tops.Count > 1, "need at least two rows to measure a pitch");
        return (tops[^1] - tops[0]) / (tops.Count - 1);
    }

    /// <summary>The header of each realized row — the row itself. A TreeViewItem's own bounds span its whole
    /// expanded subtree, so measuring those answers a different question entirely.</summary>
    private static IEnumerable<Control> Rows(TreeView tree)
        => tree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .Select(i => i.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.Name == "PART_HeaderPresenter"))
            .OfType<Control>()
            .Where(c => c.IsVisible && c.Bounds.Height > 0);

    /// <summary>A tree shaped like the schema navigator: a server with a database of columns, expanded, each
    /// row one line of text beside a 15px glyph.</summary>
    private static (Window Window, TreeView Tree) Tree(bool tightened)
    {
        var tree = new TreeView
        {
            ItemsSource = new[] { Node("local", Node("pagila", Node("id"), Node("title"), Node("year"))) },
            ItemTemplate = new FuncTreeDataTemplate<TreeNode>(
                (_, _) => new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Children =
                    {
                        new Avalonia.Controls.Shapes.Path
                        {
                            Data = Geometry.Parse("M0,0 L15,0 L15,15 L0,15 Z"),
                            Width = 15,
                            Height = 15,
                            Stroke = Brushes.Gray,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = "row",
                            FontSize = 13,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        },
                    },
                },
                n => n.Children),
        };
        if (tightened) TreeChrome.Apply(tree);

        var window = new Window { Width = 300, Height = 500, Content = tree };
        window.Show();
        window.UpdateLayout();
        foreach (var item in tree.GetVisualDescendants().OfType<TreeViewItem>()) item.IsExpanded = true;
        window.UpdateLayout();
        foreach (var item in tree.GetVisualDescendants().OfType<TreeViewItem>()) item.IsExpanded = true;
        window.UpdateLayout();
        return (window, tree);
    }

    private static TreeNode Node(string name, params TreeNode[] children) => new(name, children);

    private sealed record TreeNode(string Name, IReadOnlyList<TreeNode> Children);
}
