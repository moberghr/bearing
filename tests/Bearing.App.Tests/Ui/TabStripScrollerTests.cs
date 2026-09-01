using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
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
/// scrollbar, no wheel, no chevron, and the <c>+</c> button pushed out of reach with the tabs. The scrollbar
/// was the first fix and the wrong one — on a strip this short it is a thin target, and it says "there is
/// more" without saying more of *what*. The strip now scrolls by wheel, keeps the selection in view, and
/// reports how many tabs are off the edge so the chevron can name them.
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

        Assert.False(s.ScrollBy(300), "a strip that fits reported a scroll");

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

    /// <summary>A wheel that cannot scroll is left for someone else. The handler runs in the tunnel phase,
    /// so marking it handled anyway would kill the gesture over a strip that already fits — dead rather than
    /// merely inert (found in review).</summary>
    [Fact]
    public Task A_wheel_that_cannot_scroll_is_not_swallowed() => _ui.Run(() =>
    {
        var (window, scroller, _, s) = Strip(tabs: 1, width: 2000);

        Assert.False(s.ScrollBy(-100), "already at the left edge");
        Assert.False(s.ScrollBy(100), "nothing to scroll");
        window.Close();
    });

    /// <summary>At the far end there is nothing left to give either, so the wheel passes through there too.</summary>
    [Fact]
    public Task A_wheel_at_the_end_of_the_strip_is_not_swallowed() => _ui.Run(() =>
    {
        var (window, scroller, _, s) = Strip(tabs: 30);

        Assert.True(s.ScrollBy(100_000), "the first scroll should move it to the end");
        window.UpdateLayout();
        Assert.False(s.ScrollBy(100), "already at the right edge");
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

    // ---- the overflow count the chevron shows ----------------------------------------------------

    [Fact]
    public Task An_overflowing_strip_reports_how_many_tabs_are_off_the_edge() => _ui.Run(() =>
    {
        // The count is the chevron's whole message — "there are N more, and they are over here" — so a wrong
        // number is worse than no chevron. Read from arranged bounds, which is why this is a UI test and not
        // arithmetic (TabOverflowTests covers the arithmetic).
        var (window, scroller, strip, s) = Strip(tabs: 30);
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width, "the fixture must overflow");

        var hidden = s.HiddenCount();

        Assert.True(hidden > 0, "an overflowing strip reported nothing hidden");
        Assert.True(hidden < strip.ItemCount, $"all {strip.ItemCount} tabs reported hidden; some are on screen");
        window.Close();
    });

    [Fact]
    public Task A_strip_that_fits_reports_nothing_hidden() => _ui.Run(() =>
    {
        // Which is what hides the chevron. A chevron reading "» 0" beside a strip with room to spare is the
        // failure this pins.
        var (window, _, _, s) = Strip(tabs: 2, width: 2000);

        Assert.Equal(0, s.HiddenCount());
        window.Close();
    });

    [Fact]
    public Task Scrolling_to_the_end_still_leaves_tabs_hidden_behind() => _ui.Run(() =>
    {
        // The count must follow the scroll rather than only ever meaning "off the right". Scrolled fully
        // right, the hidden tabs are the ones behind you — and they are just as unreachable.
        var (window, _, _, s) = Strip(tabs: 30);

        s.ScrollBy(100_000);
        window.UpdateLayout();

        Assert.True(s.HiddenCount() > 0, "scrolled to the far end, the tabs behind were reported as visible");
        window.Close();
    });

    [Fact]
    public Task The_count_changes_when_the_window_is_narrowed() => _ui.Run(() =>
    {
        // The chevron is driven by an event that fires on layout, so the number has to actually move when the
        // window does — a count computed once at construction would look right until the first resize.
        var (window, _, _, s) = Strip(tabs: 30, width: 1200);
        var wide = s.HiddenCount();

        window.Width = 300;
        window.UpdateLayout();
        Pump(window);

        Assert.True(s.HiddenCount() > wide,
            $"narrowing the window from 1200 to 300 left the count at {s.HiddenCount()} (was {wide})");
        window.Close();
    });

    [Fact]
    public Task Overflow_changes_are_announced_once_per_change() => _ui.Run(() =>
    {
        // LayoutUpdated fires constantly; the event is filtered to actual changes so the chevron is not
        // re-rendered every frame. Assert both halves: that a real change is announced, and that a pass which
        // changes nothing is quiet.
        var (window, _, _, s) = Strip(tabs: 30, width: 1200);
        var announced = 0;
        s.OverflowChanged += () => announced++;

        window.Width = 300;
        window.UpdateLayout();
        Pump(window);
        Assert.True(announced > 0, "narrowing the window announced no overflow change");

        var afterResize = announced;
        window.UpdateLayout();
        Pump(window);
        Assert.Equal(afterResize, announced);
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
            // The header template the XAML uses, capped and ellipsized the same way. Without it a tab renders
            // the view model's ToString() — 539px of type name, wider than the whole viewport — so every tab
            // counted as partly hidden and the overflow count was 30 out of 30. A fixture whose tabs cannot
            // fit under any circumstances is not the strip the app has.
            ItemTemplate = new FuncDataTemplate<EditorTabViewModel>((tab, _) => new TextBlock
            {
                Text = tab?.Header,
                MaxWidth = 160,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            }, supportsRecycling: true),
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
