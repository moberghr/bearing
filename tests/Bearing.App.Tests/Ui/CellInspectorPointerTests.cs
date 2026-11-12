using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Pointer behaviour of the cell inspector's JSON text that only a realized view holds: a drag selection
/// reaching the pane's edge scrolls it, and Ctrl+click on an address raises the link — not a selection.
/// </summary>
[Collection(UiTestCollection.Name)]
public class CellInspectorPointerTests
{
    private readonly UiTestSession _ui;

    public CellInspectorPointerTests(UiTestSession ui) => _ui = ui;

    private static (Window Window, JsonTextBlock Text, ScrollViewer Scroller) Show(string json)
    {
        var view = new CellInspectorView("row[0].document", json, "jsonb", () => { });
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        Pump(window);
        var text = view.GetVisualDescendants().OfType<JsonTextBlock>().Single();
        return (window, text, text.FindAncestorOfType<ScrollViewer>()!);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private static Point InWindow(Visual from, Point at, Window window) => from.TranslatePoint(at, window)!.Value;

    [Fact]
    public Task Dragging_a_selection_to_the_right_edge_scrolls_and_extends_it() => _ui.Run(() =>
    {
        var json = $$"""{"long":"{{new string('x', 400)}}END"}""";
        var (window, text, scroller) = Show(json);
        Assert.Equal(0, scroller.Offset.X);
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width);

        var lineY = text.LineHeight * 1.5;   // the "long" line
        var start = InWindow(text, new Point(10, lineY), window);
        var edge = InWindow(scroller, new Point(scroller.Viewport.Width - 2, 0), window).WithY(start.Y);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(edge, RawInputModifiers.LeftMouseButton);
        Pump(window);
        var firstOffset = scroller.Offset.X;
        var firstEnd = text.SelectionEnd;
        window.MouseMove(edge + new Point(1, 0), RawInputModifiers.LeftMouseButton);   // still held at the edge
        Pump(window);
        window.MouseUp(edge, MouseButton.Left);

        Assert.True(firstOffset > 0);
        Assert.True(scroller.Offset.X >= firstOffset);
        Assert.True(text.SelectionEnd > text.SelectionStart);
        Assert.True(firstEnd > text.SelectionStart);
        // The selection reaches past what was visible before any scroll.
        var visibleBefore = text.TextLayout.HitTestPoint(new Point(scroller.Viewport.Width, lineY)).TextPosition;
        Assert.True(text.SelectionEnd > visibleBefore);
    });

    [Fact]
    public Task Ctrl_click_on_a_link_raises_it_and_does_not_select() => _ui.Run(() =>
    {
        var (window, text, _) = Show("""{"home":"https://example.com/x"}""");
        Uri? opened = null;
        text.LinkActivated += uri => opened = uri;
        var link = Assert.Single(text.Links);
        var rect = text.TextLayout.HitTestTextRange(link.Start + 3, 1).First();
        var at = InWindow(text, rect.Center, window);

        window.MouseDown(at, MouseButton.Left);          // plain click: no link
        window.MouseUp(at, MouseButton.Left);
        Assert.Null(opened);

        window.MouseDown(at, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.Control);

        Assert.Equal(new Uri("https://example.com/x"), opened);
        Assert.Equal(text.SelectionStart, text.SelectionEnd);
    });

    [Fact]
    public Task A_drag_can_start_in_the_indentation() => _ui.Run(() =>
    {
        var (window, text, _) = Show("""{"crew":{"pilot":"Dallas"}}""");
        var indent = new Point(2, text.LineHeight * 2.5);   // the "pilot" line, in its leading spaces

        var from = InWindow(text, indent, window);
        var to = InWindow(text, new Point(text.Bounds.Width - 4, indent.Y), window);

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);

        Assert.Equal("\"pilot\": \"Dallas\"", text.SelectedText.Trim());
    });

    [Fact]
    public Task A_triple_click_selects_the_string_under_it_not_everything() => _ui.Run(() =>
    {
        var (window, text, _) = Show("""{"title":"Alien: director's cut","year":1979}""");
        var word = text.TextLayout.HitTestTextRange(text.Text!.IndexOf("director"), 1).First();
        var at = InWindow(text, word.Center, window);

        for (var i = 0; i < 3; i++)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
        }

        Assert.Equal("Alien: director's cut", text.SelectedText);
    });

    [Fact]
    public Task Ctrl_click_beside_a_link_opens_nothing() => _ui.Run(() =>
    {
        var (window, text, _) = Show("""{"home":"https://example.com/x"}""");
        Uri? opened = null;
        text.LinkActivated += uri => opened = uri;
        var key = text.TextLayout.HitTestTextRange(text.Text!.IndexOf("home"), 1).First();

        window.MouseDown(InWindow(text, key.Center, window), MouseButton.Left, RawInputModifiers.Control);

        Assert.Null(opened);
    });
}
