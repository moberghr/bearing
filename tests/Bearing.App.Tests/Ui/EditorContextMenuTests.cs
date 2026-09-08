using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The editor's right-click menu — it had none at all, so every action on it was reachable only by a
/// gesture you had to already know. The claims here are about the realized flyout on the real shell: what
/// it offers, that the labels and gestures come from the live command table rather than literals, and that
/// a right-click puts the caret where the user pointed before the menu acts on it.
/// </summary>
[Collection(UiTestCollection.Name)]
public class EditorContextMenuTests
{
    private readonly UiTestSession _ui;

    public EditorContextMenuTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task The_editor_offers_a_menu_built_from_the_command_table() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_editor_offers_a_menu_built_from_the_command_table));
        var editor = Focused(shell);
        var flyout = Assert.IsType<MenuFlyout>(editor.TextArea.ContextFlyout);

        flyout.ShowAt(editor.TextArea);
        shell.Pump();
        var items = flyout.Items.OfType<MenuItem>().ToList();

        Assert.Contains(items, i => Header(i) == "Run");
        Assert.Contains(items, i => Header(i) == "Explain statement");
        Assert.Contains(items, i => Header(i) == "Select statement");
        Assert.Contains(items, i => Header(i) == "Toggle comment");
        Assert.Contains(items, i => Header(i) == "Paste");

        // The gesture is resolved from the keymap on open, not written into the menu — the failure the menu
        // bar's own gesture sync exists to prevent.
        Assert.Equal(KeyGesture.Parse("Ctrl+Enter"), items.First(i => Header(i) == "Run").InputGesture);
        Assert.Equal(KeyGesture.Parse("Ctrl+Shift+E"), items.First(i => Header(i) == "Explain statement").InputGesture);

        flyout.Hide();
    });

    [Fact]
    public Task A_right_click_in_the_editor_actually_opens_it() => _ui.Run(async () =>
    {
        // The whole report: right-clicking the editor did nothing. Opening the flyout by hand (as the tests
        // above do) would pass just as happily with no pointer route to it at all.
        using var shell = await ShellHarness.ShowAsync(nameof(A_right_click_in_the_editor_actually_opens_it));
        var editor = Focused(shell);
        editor.Text = "select 1;";
        shell.Pump();
        var flyout = (MenuFlyout)editor.TextArea.ContextFlyout!;
        Assert.False(flyout.IsOpen);

        RightClickLine(shell, editor, line: 1);

        Assert.True(flyout.IsOpen, "right-clicking the editor did not open its context menu");
        flyout.Hide();
    });

    [Fact]
    public Task Copy_is_offered_only_when_something_is_selected() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Copy_is_offered_only_when_something_is_selected));
        var editor = Focused(shell);
        editor.Text = "select 1 from t";
        var flyout = (MenuFlyout)editor.TextArea.ContextFlyout!;
        var copy = () => flyout.Items.OfType<MenuItem>().First(i => Header(i) == "Copy");

        flyout.ShowAt(editor.TextArea);
        shell.Pump();
        Assert.False(copy().IsEnabled);
        // Paste writes at the caret, so it stays available with nothing selected.
        Assert.True(flyout.Items.OfType<MenuItem>().First(i => Header(i) == "Paste").IsEnabled);
        flyout.Hide();

        editor.SelectionStart = 0;
        editor.SelectionLength = 6;
        flyout.ShowAt(editor.TextArea);
        shell.Pump();
        Assert.True(copy().IsEnabled);

        flyout.Hide();
    });

    [Fact]
    public Task Right_clicking_puts_the_caret_where_the_pointer_is() => _ui.Run(async () =>
    {
        // Half the menu acts on the statement under the caret (Run, Explain, Toggle comment). Without this
        // it would act on wherever the caret was last left rather than where the user is pointing.
        using var shell = await ShellHarness.ShowAsync(nameof(Right_clicking_puts_the_caret_where_the_pointer_is));
        var editor = Focused(shell);
        editor.Text = "select 1;\nselect 2;\nselect 3;";
        editor.CaretOffset = 0;
        shell.Pump();

        RightClickLine(shell, editor, line: 3);

        Assert.Equal(3, editor.TextArea.Caret.Line);
    });

    [Fact]
    public Task Right_clicking_inside_the_selection_keeps_it() => _ui.Run(async () =>
    {
        // It is the thing Copy and Toggle comment are about to be asked to act on.
        using var shell = await ShellHarness.ShowAsync(nameof(Right_clicking_inside_the_selection_keeps_it));
        var editor = Focused(shell);
        editor.Text = "select 1;\nselect 2;\nselect 3;";
        editor.SelectionStart = 0;
        editor.SelectionLength = editor.Text.Length;
        shell.Pump();

        RightClickLine(shell, editor, line: 2);

        Assert.Equal(editor.Text.Length, editor.SelectionLength);
    });

    [Fact]
    public Task Clicking_an_item_runs_the_command_behind_it() => _ui.Run(async () =>
    {
        // The menu being built from the command table is worth nothing if the item never invokes it. Toggle
        // comment is the one with a visible, synchronous result to assert on.
        using var shell = await ShellHarness.ShowAsync(nameof(Clicking_an_item_runs_the_command_behind_it));
        var editor = Focused(shell);
        editor.Text = "select 1;";
        editor.CaretOffset = 3;
        shell.Pump();

        var flyout = (MenuFlyout)editor.TextArea.ContextFlyout!;
        flyout.ShowAt(editor.TextArea);
        shell.Pump();
        flyout.Items.OfType<MenuItem>().First(i => Header(i) == "Toggle comment")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shell.Pump();

        Assert.Equal("-- select 1;", editor.Text);
        flyout.Hide();
    });

    /// <summary>Press and release the right button over the start of <paramref name="line"/> (1-based).</summary>
    private static void RightClickLine(ShellHarness shell, TextEditor editor, int line)
    {
        var view = editor.TextArea.TextView;
        var local = new Point(4, (view.DefaultLineHeight * (line - 0.5)) - view.VerticalOffset);
        var point = view.TranslatePoint(local, shell.Window)
                    ?? throw new Xunit.Sdk.XunitException("the text view is not in the window's visual tree");

        shell.Window.MouseDown(point, MouseButton.Right);
        shell.Window.MouseUp(point, MouseButton.Right);
        shell.Pump();
    }

    private static string? Header(MenuItem item) => item.Header as string;

    /// <summary>The shell's editor, focused — the handlers only run on something that genuinely has focus.</summary>
    private static TextEditor Focused(ShellHarness shell)
    {
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        shell.Pump();
        Assert.True(editor.TextArea.IsFocused, "the editor never took focus");
        return editor;
    }
}
