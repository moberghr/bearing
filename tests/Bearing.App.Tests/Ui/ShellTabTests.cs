using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Tab behaviour that only holds with the window attached — the half of #87 and #88 no view-model test could
/// reach. The shell constructs headlessly, so the real tab strip, its two-way selection binding and keyboard
/// focus are all in play.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ShellTabTests
{
    private readonly UiTestSession _ui;

    public ShellTabTests(UiTestSession ui) => _ui = ui;

    /// <summary>#88: a new tab takes the caret. The ＋ button used to leave focus on itself, so the first
    /// thing typed after opening a tab went nowhere.</summary>
    [Fact]
    public Task A_new_tab_puts_the_caret_in_the_editor() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_new_tab_puts_the_caret_in_the_editor));
        var editor = Editor(shell);

        // Park focus somewhere else first, so the assertion cannot pass by accident.
        NewTabButton(shell).Focus();
        shell.Pump();
        Assert.False(editor.TextArea.IsFocused, "the fixture must start with focus off the editor");

        shell.Window.NewTabAndFocus();
        shell.Pump();

        Assert.True(editor.TextArea.IsFocused, "opening a tab left the caret outside the editor");
    });

    /// <summary>…and the tab it focused is the one it just created.</summary>
    [Fact]
    public Task A_new_tab_is_the_selected_one() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_new_tab_is_the_selected_one));
        var before = shell.Vm.Workspace.SelectedTab;

        shell.Window.NewTabAndFocus();
        shell.Pump();

        Assert.NotSame(before, shell.Vm.Workspace.SelectedTab);
        Assert.Same(shell.Vm.Workspace.Tabs.Last(), shell.Vm.Workspace.SelectedTab);
    });

    /// <summary>
    /// #87 end to end, in the configuration the bug needed: with the real strip bound, closing a tab lands on
    /// its left-hand neighbour. The control fixes up its own selection during the removal and writes that
    /// back through the binding, which is why every view-model test agreed with the broken code.
    /// </summary>
    [Fact]
    public Task Closing_a_tab_selects_its_left_neighbour_with_the_strip_bound() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(
            nameof(Closing_a_tab_selects_its_left_neighbour_with_the_strip_bound));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var tabs = Enumerable.Range(1, 4).Select(i => workspace.NewTab($"-- {i}")).ToArray();
        workspace.SelectedTab = tabs[2];
        shell.Pump();

        // The binding really is live: the strip shows what the view-model selected.
        var strip = shell.Window.GetVisualDescendants().OfType<TabStrip>().First();
        Assert.Same(tabs[2], strip.SelectedItem);

        Assert.True(await workspace.CloseTabAsync(tabs[2]));
        shell.Pump();

        Assert.Same(tabs[1], workspace.SelectedTab);
        Assert.Same(tabs[1], strip.SelectedItem);
    });

    /// <summary>And the editor shows the tab the close landed on, which is the thing the user sees.</summary>
    [Fact]
    public Task The_editor_shows_the_tab_the_close_landed_on() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_editor_shows_the_tab_the_close_landed_on));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        workspace.NewTab("-- first");
        var second = workspace.NewTab("-- second");
        var third = workspace.NewTab("-- third");
        workspace.SelectedTab = third;
        shell.Pump();

        Assert.True(await workspace.CloseTabAsync(third));
        shell.Pump();

        Assert.Same(second, workspace.SelectedTab);
        Assert.Equal("-- second", Editor(shell).Text);
    });

    /// <summary>The ＋ button is wired to the same path as the command, so clicking it focuses too — the
    /// gesture actually reported in #88.</summary>
    [Fact]
    public Task Clicking_the_plus_button_focuses_the_editor() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Clicking_the_plus_button_focuses_the_editor));
        var editor = Editor(shell);
        var button = NewTabButton(shell);
        button.Focus();
        shell.Pump();
        var before = shell.Vm.Workspace.Tabs.Count;

        // The Click routed event rather than a synthetic press: presses do not reach a control's handler
        // headlessly (§4.5), and what is under test is the wiring behind the button — the XAML handler —
        // not Avalonia's hit-testing.
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.Pump();

        Assert.Equal(before + 1, shell.Vm.Workspace.Tabs.Count);
        Assert.True(editor.TextArea.IsFocused, "the + button left the caret outside the editor");
    });

    // ---- pinned row (#67) -----------------------------------------------------------------------

    /// <summary>The pinned row is hidden while nothing is pinned, and appears when something is.</summary>
    [Fact]
    public Task The_pinned_row_appears_only_when_something_is_pinned() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_pinned_row_appears_only_when_something_is_pinned));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var tabs = Enumerable.Range(1, 3).Select(i => workspace.NewTab($"-- {i}")).ToArray();
        shell.Pump();

        var pinnedRow = Row(shell, "PinnedTabScroll");
        Assert.False(pinnedRow.IsVisible);

        workspace.SetPinned(tabs[1], true);
        shell.Pump();

        Assert.True(pinnedRow.IsVisible);
    });

    /// <summary>
    /// Only one row draws a selection, because there is one selected tab.
    /// <para>
    /// Asserted on the rendered look rather than on <c>SelectedItem</c>: a <c>TabStrip</c> is
    /// always-selected, so the row that does not hold the selected tab keeps a selection of its own and
    /// cannot be cleared. What matters is that it does not <i>look</i> selected — which is the bug a user
    /// would see, two highlighted tabs at once.
    /// </para>
    /// </summary>
    [Fact]
    public Task Only_one_row_draws_a_selection() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Only_one_row_draws_a_selection));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var plain = workspace.NewTab("-- plain");
        var kept = workspace.NewTab("-- kept");
        workspace.SetPinned(kept, true);
        shell.Pump();

        workspace.SelectedTab = kept;
        shell.Pump();
        Assert.True(DrawsSelection(shell, "PinnedTabStrip"), "the pinned row holds the selection");
        Assert.False(DrawsSelection(shell, "TabStrip"), "the strip drew a selection it does not hold");

        workspace.SelectedTab = plain;
        shell.Pump();
        Assert.True(DrawsSelection(shell, "TabStrip"), "the strip holds the selection");
        Assert.False(DrawsSelection(shell, "PinnedTabStrip"), "the pinned row drew a selection it does not hold");
    });

    /// <summary>Whether a row is drawing its selected tab as selected — the semibold, accented look.</summary>
    private static bool DrawsSelection(ShellHarness shell, string strip)
    {
        var item = Strip(shell, strip).GetVisualDescendants().OfType<TabStripItem>().FirstOrDefault(i => i.IsSelected);
        return item is not null && item.FontWeight == FontWeight.SemiBold;
    }

    /// <summary>Pinning the selected tab keeps it selected — it moves row, it does not lose the caret.</summary>
    [Fact]
    public Task Pinning_the_selected_tab_keeps_it_selected() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Pinning_the_selected_tab_keeps_it_selected));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        workspace.NewTab("-- other");
        var kept = workspace.NewTab("-- kept");
        workspace.SelectedTab = kept;
        shell.Pump();

        workspace.SetPinned(kept, true);
        shell.Pump();

        Assert.Same(kept, workspace.SelectedTab);
        Assert.True(DrawsSelection(shell, "PinnedTabStrip"), "the pinned row should show the tab it now holds");
        Assert.Equal("-- kept", Editor(shell).Text);
    });

    /// <summary>A pinned tab has no ✕, so it cannot be closed by a click two pixels from its name.</summary>
    [Fact]
    public Task A_pinned_tab_has_no_close_button() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_pinned_tab_has_no_close_button));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var kept = workspace.NewTab("-- kept");
        workspace.SetPinned(kept, true);
        shell.Pump();

        // Found by Tag, which is what the production code keys on (IsCloseAffordance). This used to count
        // any 16x16 border and broke the moment the row gained a pin toggle of the same size — a dimension
        // is not an identity, and the proxy outlived its accuracy.
        var affordances = Strip(shell, "PinnedTabStrip")
            .GetVisualDescendants()
            .OfType<Border>()
            .Select(b => b.Tag as string)
            .Where(t => t is not null)
            .ToList();

        Assert.DoesNotContain("close", affordances);
        // …and the row does have the one affordance it needs: the pinned template has no ✕ by design, so
        // unpinning is the only way out of it, and it must be reachable by mouse.
        Assert.Contains("pin", affordances);
    });

    /// <summary>
    /// A press in a tab's own padding selects it, like a press on its name. A TabStripItem is padded 10x5, so
    /// a handler in the item template missed those pixels while the strip still moved its own SelectedIndex —
    /// the header lit up in a row whose tab was never selected, and nothing corrected it (#67 review).
    /// </summary>
    [Fact]
    public Task A_press_in_a_tab_header_s_padding_still_selects_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_press_in_a_tab_header_s_padding_still_selects_it));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var first = workspace.NewTab("-- one");
        var second = workspace.NewTab("-- two");
        workspace.SelectedTab = first;
        shell.Pump();

        var item = Item(shell, "TabStrip", second);
        // Two pixels in from the item's own corner: inside its padding, outside the template's content.
        Press(shell, item, new Point(2, 2));

        Assert.Same(second, workspace.SelectedTab);
        Assert.True(item.IsSelected);
    });

    /// <summary>The same hole in the pinned row, where the selection also has to cross rows.</summary>
    [Fact]
    public Task A_press_in_a_pinned_tab_s_padding_selects_across_the_rows() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_press_in_a_pinned_tab_s_padding_selects_across_the_rows));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var kept = workspace.NewTab("-- kept");
        var scratch = workspace.NewTab("-- scratch");
        workspace.SetPinned(kept, true);
        workspace.SelectedTab = scratch;
        shell.Pump();

        Press(shell, Item(shell, "PinnedTabStrip", kept), new Point(2, 2));

        Assert.Same(kept, workspace.SelectedTab);
        Assert.True(DrawsSelection(shell, "PinnedTabStrip"), "the pinned row should now hold the selection");
        Assert.False(DrawsSelection(shell, "TabStrip"), "the strip should have stopped drawing one");
    });

    /// <summary>
    /// Moving the selection with the keyboard inside a focused strip reaches the view model. The strip's own
    /// selection changing is not enough — the editor buffer and the results pane follow the view model.
    /// </summary>
    [Fact]
    public Task Moving_a_strip_s_own_selection_moves_the_workspace() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Moving_a_strip_s_own_selection_moves_the_workspace));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var first = workspace.NewTab("-- one");
        var second = workspace.NewTab("-- two");
        workspace.SelectedTab = first;
        shell.Pump();

        // What an arrow key does inside the strip that holds the selection.
        Strip(shell, "TabStrip").SelectedItem = second;
        shell.Pump();

        Assert.Same(second, workspace.SelectedTab);
    });

    /// <summary>
    /// …but not from the row that does not hold it. That row re-asserts a selection of its own whenever its
    /// items change, and honouring it is how pinning the selected tab used to jump the workspace to a
    /// different tab (#67).
    /// </summary>
    [Fact]
    public Task The_dormant_row_cannot_move_the_workspace() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_dormant_row_cannot_move_the_workspace));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var kept = workspace.NewTab("-- kept");
        var scratch = workspace.NewTab("-- scratch");
        workspace.SetPinned(kept, true);
        workspace.SelectedTab = scratch;
        shell.Pump();

        Strip(shell, "PinnedTabStrip").SelectedItem = kept;
        shell.Pump();

        Assert.Same(scratch, workspace.SelectedTab);
    });

    /// <summary>Renaming a pinned tab has a box to type in — and one that can clear the renaming state
    /// again. Looking for it only in the unpinned row left a pinned tab stuck mid-rename (#67 review).</summary>
    [Fact]
    public Task A_pinned_tab_can_be_renamed() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_pinned_tab_can_be_renamed));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var kept = workspace.NewTab("-- kept");
        workspace.SetPinned(kept, true);
        workspace.SelectedTab = kept;
        shell.Pump();

        kept.BeginRename();
        shell.Pump();

        var box = Strip(shell, "PinnedTabStrip")
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(b => ReferenceEquals(b.DataContext, kept));
        Assert.NotNull(box);
        Assert.True(box!.IsVisible, "the rename box is in the template but never shown");

        // And it can be got out of again, which is the half that was actually broken: without a box, F2 set
        // IsRenaming and nothing was left that could clear it. Esc goes through the box's own handler.
        box.Focus();
        shell.Pump();
        shell.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        shell.Pump();

        Assert.False(kept.IsRenaming, "Esc in the pinned rename box did not end the rename");
    });

    /// <summary>A run in flight shows in the pinned row too. It is the row holding the scripts you keep
    /// coming back to, so it is the likeliest to have a long query running (#67 review).</summary>
    [Fact]
    public Task A_running_pinned_tab_shows_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_running_pinned_tab_shows_it));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var kept = workspace.NewTab("-- kept");
        workspace.SetPinned(kept, true);
        shell.Pump();

        var spinner = Indicator(shell, kept);
        Assert.False(spinner.IsVisible, "the indicator shows on an idle tab");

        kept.IsRunning = true;
        shell.Pump();

        Assert.True(Indicator(shell, kept).IsVisible, "a running pinned tab looks idle");
    });

    /// <summary>The running indicator of a tab: the rotating arc in its header.</summary>
    private static Shapes.Path Indicator(ShellHarness shell, EditorTabViewModel tab)
        => Item(shell, tab.IsPinned ? "PinnedTabStrip" : "TabStrip", tab)
            .GetVisualDescendants()
            .OfType<Shapes.Path>()
            .First(p => ToolTip.GetTip(p) as string == "Running…");

    /// <summary>A tab's container in a named strip.</summary>
    private static TabStripItem Item(ShellHarness shell, string strip, EditorTabViewModel tab)
        => Strip(shell, strip)
            .GetVisualDescendants()
            .OfType<TabStripItem>()
            .First(i => ReferenceEquals(i.DataContext, tab));

    private static void Press(ShellHarness shell, Control target, Point at)
    {
        var point = target.TranslatePoint(at, shell.Window)
                    ?? throw new InvalidOperationException("the control is not in the window");
        shell.Window.MouseMove(point);
        shell.Window.MouseDown(point, MouseButton.Left);
        shell.Window.MouseUp(point, MouseButton.Left);
        shell.Pump();
    }

    /// <summary>
    /// A middle-click on the ✕ closes the tab, like a middle-click anywhere else on it.
    /// <para>
    /// The gap this closes: the strip-level press handler returns early on the close affordance so a left
    /// click does not select the tab on its way to closing it — and the ✕ itself ignores everything but the
    /// left button (#66). Between them, a middle-click landing on the glyph did nothing while one two pixels
    /// away worked. <c>TabPointerGestures</c> was tested in isolation and had nothing to say about the
    /// interaction, which is where the bug was.
    /// </para>
    /// </summary>
    [Fact]
    public Task A_middle_click_on_the_close_glyph_closes_the_tab() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_middle_click_on_the_close_glyph_closes_the_tab));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var keep = workspace.NewTab("-- keep");
        var doomed = workspace.NewTab("-- doomed");
        shell.Pump();

        var closer = Item(shell, "TabStrip", doomed)
            .GetVisualDescendants()
            .OfType<Border>()
            .First(b => b.Tag as string == "close");

        // Asserted on the *routing*, not on the tab having gone. The handler marks the press handled and then
        // awaits the close, and whether that await has settled by the time the assertion runs turned out to
        // depend on what an earlier test in this collection left on the dispatcher — the tab does close, and
        // it closed reliably when this ran alone, but the completion is not what the fix changed. What it
        // changed is that a middle press on the ✕ is no longer swallowed by the early return, and that is
        // exactly what "handled" records.
        var handled = false;
        shell.Window.AddHandler(InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) => handled = e.Handled,
            RoutingStrategies.Bubble, handledEventsToo: true);

        PressWith(shell, closer, new Point(8, 8), MouseButton.Middle);

        Assert.True(handled, "a middle press on the ✕ was ignored — the close affordance swallowed it again");
    });

    /// <summary>
    /// A left press on the ✕ does not select the tab on its way to closing it.
    /// <para>
    /// Selection is the assertable half, and deliberately the only one asserted. It moves — or does not — in
    /// the strip's <b>synchronous</b> press handler, so the answer is settled by the time the press returns.
    /// The close itself runs through an <c>async void</c> handler, and §4.5 records what asserting its
    /// completion from a UI test costs: this test used to end with <c>DoesNotContain(first, Tabs)</c>, passed
    /// on its own, and failed roughly one full-suite run in three depending on what an earlier test in the
    /// collection had left on the shared dispatcher. The close is covered where it can be awaited —
    /// <c>CloseTabPromptTests</c>, <c>AutosaveModeTests</c> and <c>BackgroundExecutionTests</c> all await
    /// <c>CloseTabAsync</c> directly — so nothing is lost by not racing it here.
    /// </para>
    /// <para>
    /// And selection is the part that matters: selecting the tab being closed would leave #87's
    /// neighbour rule choosing from the wrong index.
    /// </para>
    /// </summary>
    [Fact]
    public Task A_left_click_on_the_close_glyph_does_not_select_the_tab_first() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_left_click_on_the_close_glyph_does_not_select_the_tab_first));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var first = workspace.NewTab("-- one");
        workspace.NewTab("-- two");
        var third = workspace.NewTab("-- three");
        workspace.SelectedTab = third;
        shell.Pump();

        var closer = Item(shell, "TabStrip", first)
            .GetVisualDescendants()
            .OfType<Border>()
            .First(b => b.Tag as string == "close");

        var handled = false;
        shell.Window.AddHandler(InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) => handled = e.Handled,
            RoutingStrategies.Bubble, handledEventsToo: true);

        PressWith(shell, closer, new Point(8, 8), MouseButton.Left);

        Assert.True(handled, "a left press on the ✕ was ignored");
        // Never moved to the tab being closed, so it stayed where the user had it.
        Assert.Same(third, workspace.SelectedTab);
    });

    /// <summary>
    /// Press a control with a given button and let the resulting work finish.
    /// <para>
    /// The drain is unfiltered (<c>RunJobs()</c> with no priority) and repeated, unlike
    /// <see cref="ShellHarness.Pump"/>, which stops at <c>Loaded</c>. Closing a tab runs through an
    /// <c>async void</c> handler whose continuations post at ordinary priority, and they can queue behind work
    /// an earlier test in this collection left on the shared dispatcher — which is how this assertion passed
    /// alone and failed in the class.
    /// </para>
    /// </summary>
    private static void PressWith(ShellHarness shell, Control target, Point at, MouseButton button)
    {
        var point = target.TranslatePoint(at, shell.Window)
                    ?? throw new InvalidOperationException("the control is not in the window");
        shell.Window.MouseMove(point);
        shell.Window.MouseDown(point, button);
        shell.Window.MouseUp(point, button);
        for (var i = 0; i < 5; i++)
        {
            shell.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Control Row(ShellHarness shell, string name)
        => shell.Window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

    private static TabStrip Strip(ShellHarness shell, string name)
        => shell.Window.GetVisualDescendants().OfType<TabStrip>().First(s => s.Name == name);

    private static Button NewTabButton(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "+");

    /// <summary>The SQL editor, by name. There is more than one TextEditor in the shell — the sidebar's
    /// history preview is one — so taking the first is a coin toss.</summary>
    private static TextEditor Editor(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");

    // ---- the pin toggle ---------------------------------------------------------------------------

    /// <summary>
    /// The pin is hidden until the pointer is on its tab, and a pinned tab keeps its pin regardless.
    /// <para>
    /// Hover-only because always-visible pins would put a permanent mark on every tab of a strip that was
    /// deliberately narrowed; kept visible while pinned because that row's pin is the only thing saying why
    /// the row exists, and the only place the unpin action lives.
    /// </para>
    /// </summary>
    [Fact]
    public Task The_pin_is_revealed_by_hovering_its_tab() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_pin_is_revealed_by_hovering_its_tab));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var first = workspace.NewTab("-- one");
        var second = workspace.NewTab("-- two");
        workspace.SelectedTab = first;
        shell.Pump();

        Assert.All(Pins(shell), pin => Assert.Equal(0, pin.Opacity));

        Hover(shell, second);

        Assert.True(Pin(shell, second).Opacity > 0, "hovering a tab did not reveal its pin");
        Assert.Equal(0, Pin(shell, first).Opacity);
        // An invisible 16px target would otherwise swallow presses aimed at the tab itself.
        Assert.False(Pin(shell, first).IsHitTestVisible);
    });

    [Fact]
    public Task A_pinned_tabs_pin_stays_visible_without_hovering() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_pinned_tabs_pin_stays_visible_without_hovering));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var kept = workspace.NewTab("-- kept");
        workspace.NewTab("-- other");
        workspace.SetPinned(kept, true);
        shell.Pump();

        var pin = Pin(shell, kept);
        Assert.True(pin.Opacity > 0, "a pinned tab's pin is invisible");
        Assert.True(pin.IsHitTestVisible);
        Assert.Contains("pinned", pin.Classes);
    });

    [Fact]
    public Task Clicking_the_pin_pins_the_tab_without_selecting_it() => _ui.Run(async () =>
    {
        // Pinning a tab you are not on is an ordinary thing to want, and dragging the selection along with
        // it would be a surprise — which is why the strip's tunnel handler has to leave this press alone.
        using var shell = await ShellHarness.ShowAsync(nameof(Clicking_the_pin_pins_the_tab_without_selecting_it));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var selected = workspace.NewTab("-- selected");
        var other = workspace.NewTab("-- other");
        workspace.SelectedTab = selected;
        shell.Pump();

        Hover(shell, other);
        PressWith(shell, Pin(shell, other), new Point(8, 8), MouseButton.Left);

        Assert.True(other.IsPinned, "the pin did not pin the tab");
        Assert.Same(selected, workspace.SelectedTab);
        Assert.Contains(other, workspace.PinnedTabs);
    });

    [Fact]
    public Task Clicking_a_pinned_tabs_pin_unpins_it() => _ui.Run(async () =>
    {
        // The pinned row has no ✕ by design, so this toggle is the only mouse route out of it.
        using var shell = await ShellHarness.ShowAsync(nameof(Clicking_a_pinned_tabs_pin_unpins_it));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var kept = workspace.NewTab("-- kept");
        workspace.NewTab("-- other");
        workspace.SetPinned(kept, true);
        shell.Pump();

        PressWith(shell, Pin(shell, kept), new Point(8, 8), MouseButton.Left);

        Assert.False(kept.IsPinned);
        Assert.Contains(kept, workspace.UnpinnedTabs);
    });

    [Fact]
    public Task A_middle_press_on_the_pin_does_not_pin() => _ui.Run(async () =>
    {
        // Middle-click anywhere on a header is the close gesture. The pin takes the left button only, the
        // same rule the ✕ follows (#66) — otherwise a middle-click aimed at closing would pin instead.
        using var shell = await ShellHarness.ShowAsync(nameof(A_middle_press_on_the_pin_does_not_pin));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var first = workspace.NewTab("-- one");
        var second = workspace.NewTab("-- two");
        workspace.SelectedTab = first;
        shell.Pump();

        Hover(shell, second);
        PressWith(shell, Pin(shell, second), new Point(8, 8), MouseButton.Middle);

        Assert.False(second.IsPinned, "a middle press on the pin pinned the tab");
    });

    private static System.Collections.Generic.List<Border> Pins(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Tag as string == "pin").ToList();

    private static Border Pin(ShellHarness shell, EditorTabViewModel tab)
        => Pins(shell).First(b => ReferenceEquals(b.DataContext, tab));

    /// <summary>Move the pointer onto a tab's header, which is what reveals its pin.</summary>
    private static void Hover(ShellHarness shell, EditorTabViewModel tab)
    {
        var item = shell.Window.GetVisualDescendants().OfType<TabStripItem>()
            .First(i => ReferenceEquals(i.DataContext, tab));
        var centre = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), shell.Window)
                     ?? throw new InvalidOperationException("the tab is not in the window");
        shell.Window.MouseMove(centre);
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
    }

    // ---- double-click the empty strip -------------------------------------------------------------

    /// <summary>
    /// A double-click on the empty part of the strip opens a tab, as every browser does.
    /// <para>
    /// Raised on the scroller, which is where that empty space lives: with few tabs the TabStrip is only as
    /// wide as its items, so everything to the right of the last tab belongs to the scroller around it.
    /// </para>
    /// </summary>
    [Fact]
    public Task A_double_click_on_the_empty_strip_opens_a_tab() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_double_click_on_the_empty_strip_opens_a_tab));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        workspace.SelectedTab = workspace.NewTab("-- one");
        shell.Pump();
        var before = workspace.Tabs.Count;

        Row(shell, "TabScroll").RaiseEvent(new TappedEventArgs(Control.DoubleTappedEvent, null!));
        shell.Pump();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + 1, workspace.Tabs.Count);
    });

    /// <summary>
    /// …and a double-click on a tab still renames it rather than opening one.
    /// <para>
    /// The gesture on a tab already meant "rename" (#39), and the new handler sits on an ancestor, so a
    /// double-click on a header reaches both. Without the source guard it would start a rename and then open
    /// a tab on top of it.
    /// </para>
    /// </summary>
    [Fact]
    public Task A_double_click_on_a_tab_renames_it_and_opens_nothing() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_double_click_on_a_tab_renames_it_and_opens_nothing));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        var only = workspace.NewTab("-- one");
        workspace.SelectedTab = only;
        shell.Pump();
        var before = workspace.Tabs.Count;

        var item = shell.Window.GetVisualDescendants().OfType<TabStripItem>()
            .First(i => ReferenceEquals(i.DataContext, only));
        var header = item.GetVisualDescendants().OfType<TextBlock>().First();
        header.RaiseEvent(new TappedEventArgs(Control.DoubleTappedEvent, null!));
        shell.Pump();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, workspace.Tabs.Count);
        Assert.True(only.IsRenaming, "a double-click on a tab no longer starts a rename");
    });
}
