using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
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

    private static Button NewTabButton(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "+");

    /// <summary>The SQL editor, by name. There is more than one TextEditor in the shell — the sidebar's
    /// history preview is one — so taking the first is a coin toss.</summary>
    private static TextEditor Editor(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
}
