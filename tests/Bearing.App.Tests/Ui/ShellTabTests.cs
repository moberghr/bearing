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

        // The ✕ is a 16x16 transparent border in the unpinned template; the pinned one has none.
        var closers = Strip(shell, "PinnedTabStrip")
            .GetVisualDescendants()
            .OfType<Border>()
            .Count(b => b is { Width: 16, Height: 16 });
        Assert.Equal(0, closers);
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
}
