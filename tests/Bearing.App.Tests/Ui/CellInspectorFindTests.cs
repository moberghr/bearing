using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Find in the cell inspector marks hits on a layer behind the text instead of re-splitting its runs: a
/// keystroke used to rebuild and re-lay-out the whole document (over a second per character on 20 KB).
/// What can only be held on a realized view: the text survives a query, the marks land on the hits, and
/// stepping through them scrolls the current one into view.
/// </summary>
[Collection(UiTestCollection.Name)]
public class CellInspectorFindTests
{
    private readonly UiTestSession _ui;

    public CellInspectorFindTests(UiTestSession ui) => _ui = ui;

    private const string Json = """{"id":"nationality","flags":["Contact.Nationality"],"level":0}""";

    private sealed record Shown(Window Window, CellInspectorView View, TextBox Find, JsonTextBlock Text,
        FindHighlightLayer Layer)
    {
        public void Pump()
        {
            for (var i = 0; i < 3; i++)
            {
                Window.UpdateLayout();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        }

        public void Type(string text)
        {
            Find.Focus();
            Pump();
            Assert.True(Find.IsFocused);
            Window.KeyTextInput(text);
            Pump();
        }

        public void Enter(RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.KeyPress(Key.Enter, modifiers, PhysicalKey.Enter, null);
            Window.KeyRelease(Key.Enter, modifiers, PhysicalKey.Enter, null);
            Pump();
        }

        public string Count => View.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").First(t => t.Contains(" of ") || t == "no matches");

        public Button Button(string tip) => View.GetVisualDescendants().OfType<Button>()
            .Single(b => ToolTip.GetTip(b) as string == tip);

        public ScrollViewer Scroller => Text.FindAncestorOfType<ScrollViewer>()!;
    }

    private static Shown Show(string json, double height = 600)
    {
        var view = new CellInspectorView("row[0].document", json, "jsonb", () => { });
        var window = new Window { Width = 700, Height = height, Content = view };
        window.Show();
        var descendants = view.GetVisualDescendants().ToList();
        var shown = new Shown(window, view,
            descendants.OfType<TextBox>().Single(t => t.PlaceholderText == "Find in value…"),
            descendants.OfType<JsonTextBlock>().Single(),
            descendants.OfType<FindHighlightLayer>().Single());
        shown.Pump();
        return shown;
    }

    [Fact]
    public Task Typing_a_query_marks_hits_without_relaying_out_the_document() => _ui.Run(() =>
    {
        var shown = Show(Json);
        var layout = shown.Text.TextLayout;

        shown.Type("natio");

        Assert.Equal("1 of 2", shown.Count);
        Assert.Same(layout, shown.Text.TextLayout);         // the text was not touched
        var marks = shown.Layer.MatchRectangles().Select(m => m.Rect).ToList();
        Assert.Equal(2, marks.Count);
        var rowHeight = shown.Text.LineHeight;
        Assert.Equal(1 * rowHeight, marks[0].Top, 1);       //   "id": "nationality",
        Assert.Equal(3 * rowHeight, marks[1].Top, 1);       //     "Contact.Nationality"
        Assert.All(marks, m => Assert.True(m.Width > 0));
        Assert.True(marks[1].Left > marks[0].Left);         // deeper indent, longer prefix
    });

    [Fact]
    public Task A_hit_inside_a_collapsed_container_opens_it_and_is_marked() => _ui.Run(() =>
    {
        var shown = Show(Json);
        shown.Button("Collapse all").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shown.Pump();
        Assert.Contains("[…1…]", shown.Text.Text);

        shown.Type("contact");

        Assert.Equal("1 of 1", shown.Count);
        Assert.DoesNotContain("[…1…]", shown.Text.Text);
        Assert.Single(shown.Layer.MatchRectangles());
    });

    [Fact]
    public Task Enter_and_the_arrows_step_through_the_hits_and_wrap() => _ui.Run(() =>
    {
        var shown = Show(Json);
        shown.Type("natio");
        var first = shown.Layer.CurrentRectangle();

        shown.Enter();
        Assert.Equal("2 of 2", shown.Count);
        Assert.NotEqual(first, shown.Layer.CurrentRectangle());

        shown.Enter();
        Assert.Equal("1 of 2", shown.Count);                // wraps past the last
        Assert.Equal(first, shown.Layer.CurrentRectangle());

        shown.Enter(RawInputModifiers.Shift);
        Assert.Equal("2 of 2", shown.Count);                // and back past the first

        shown.Button("Previous match (Shift+Enter)").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shown.Pump();
        Assert.Equal("1 of 2", shown.Count);
        shown.Button("Next match (Enter)").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shown.Pump();
        Assert.Equal("2 of 2", shown.Count);
    });

    [Fact]
    public Task The_current_hit_is_scrolled_into_view() => _ui.Run(() =>
    {
        var json = new StringBuilder("[");
        for (var i = 0; i < 200; i++) json.Append($"{{\"n\":{i}}},");
        json.Append("{\"needle\":1},{\"needle\":2}]");
        var shown = Show(json.ToString(), height: 400);
        Assert.Equal(0, shown.Scroller.Offset.Y);

        shown.Type("needle");
        shown.Pump();

        Assert.Equal("1 of 2", shown.Count);
        AssertInView(shown);

        shown.Enter();
        AssertInView(shown);
    });

    private static void AssertInView(Shown shown)
    {
        var hit = shown.Layer.CurrentRectangle()!.Value;
        var top = shown.Layer.TranslatePoint(hit.TopLeft, shown.Scroller)!.Value.Y;
        Assert.True(shown.Scroller.Offset.Y > 0);
        Assert.InRange(top, 0, shown.Scroller.Viewport.Height - hit.Height);
    }
}
