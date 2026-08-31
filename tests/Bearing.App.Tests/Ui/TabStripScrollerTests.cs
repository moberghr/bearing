using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Reaching the tabs that fall off the right edge (#65). The strip sat in a horizontal StackPanel, which
/// measures at infinite width, so the overflow was laid out and clipped with nothing to scroll it — no
/// scrollbar, no wheel, no chevrons, and the <c>+</c> button pushed out of reach with the tabs.
/// <para>
/// The strip is assembled here the way the XAML assembles it (a ScrollViewer around a TabStrip), because the
/// behaviour under test is the scroller's, not the window's.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class TabStripScrollerTests
{
    private readonly UiTestSession _ui;

    public TabStripScrollerTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task An_overflowing_strip_can_be_scrolled() => _ui.Run(() =>
    {
        var (window, scroller, _, s) = Strip(tabs: 30);
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width, "the fixture must overflow");
        Assert.Equal(0, scroller.Offset.X);

        s.ScrollBy(200);
        window.UpdateLayout();

        Assert.Equal(200, scroller.Offset.X);
        window.Close();
    });

    [Fact]
    public Task Scrolling_stops_at_both_ends() => _ui.Run(() =>
    {
        var (window, scroller, _, s) = Strip(tabs: 30);

        s.ScrollBy(-500);
        Assert.Equal(0, scroller.Offset.X);

        s.ScrollBy(100_000);
        window.UpdateLayout();
        Assert.Equal(scroller.Extent.Width - scroller.Viewport.Width, scroller.Offset.X, 1);
        window.Close();
    });

    /// <summary>A strip that already fits has nowhere to go, and must not be nudged off its own left edge.</summary>
    [Fact]
    public Task A_strip_that_fits_does_not_move() => _ui.Run(() =>
    {
        var (window, scroller, _, s) = Strip(tabs: 1, width: 2000);
        Assert.False(scroller.Extent.Width > scroller.Viewport.Width,
            $"the fixture must not overflow: extent {scroller.Extent.Width}, viewport {scroller.Viewport.Width}");

        s.ScrollBy(300);

        Assert.Equal(0, scroller.Offset.X);
        window.Close();
    });

    /// <summary>The wheel scrolls sideways: there is nothing to scroll vertically, and a vertical wheel over
    /// a horizontal strip still means "move along it".</summary>
    [Fact]
    public Task The_wheel_scrolls_the_strip_sideways() => _ui.Run(() =>
    {
        var (window, scroller, _, _) = Strip(tabs: 30);
        var point = scroller.TranslatePoint(new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2), window);
        Assert.NotNull(point);

        window.MouseMove(point!.Value);
        window.MouseWheel(point.Value, new Vector(0, -1));
        window.UpdateLayout();

        Assert.True(scroller.Offset.X > 0, $"the wheel left the strip at {scroller.Offset.X}");

        var scrolled = scroller.Offset.X;
        window.MouseWheel(point.Value, new Vector(0, 1));
        window.UpdateLayout();
        Assert.True(scroller.Offset.X < scrolled, "the wheel does not scroll back");
        window.Close();
    });

    /// <summary>Selecting a tab off the right edge brings it into view — otherwise Ctrl+Tab and Ctrl+PageDown
    /// land on a tab nobody can see, which is the half of #65 the keyboard already worked around.</summary>
    [Fact]
    public Task Selecting_an_offscreen_tab_brings_it_into_view() => _ui.Run(() =>
    {
        var (window, scroller, strip, s) = Strip(tabs: 30);
        Assert.Equal(0, scroller.Offset.X);

        strip.SelectedIndex = 29;
        window.UpdateLayout();
        s.BringSelectionIntoView();
        Pump(window);

        var atLastTab = scroller.Offset.X;
        Assert.True(atLastTab > 0, "the last tab was selected and the strip never moved");

        // …and back the other way, so this is "follow the selection", not "scroll to the end".
        strip.SelectedIndex = 0;
        window.UpdateLayout();
        s.BringSelectionIntoView();
        Pump(window);

        Assert.True(scroller.Offset.X < atLastTab,
            $"selecting the first tab left the strip at {scroller.Offset.X}, no closer than {atLastTab}");
        window.Close();
    });

    private static (Window Window, ScrollViewer Scroller, TabStrip Strip, TabStripScroller Scrolling)
        Strip(int tabs, double width = 400)
    {
        var strip = new TabStrip
        {
            ItemsSource = Enumerable.Range(1, tabs)
                .Select(i => new EditorTabViewModel($"query-{i:00}.sql"))
                .ToList(),
        };
        var scroller = new ScrollViewer
        {
            Content = strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var scrolling = new TabStripScroller(scroller, strip);

        var window = new Window { Width = width, Height = 120, Content = scroller };
        window.Show();
        Pump(window);
        return (window, scroller, strip, scrolling);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        }
    }
}
