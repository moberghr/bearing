using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Auto-close as the user meets it (#70): real text input into the real editor in the real shell. The
/// decisions are covered exhaustively and cheaply by <c>Bearing.Sql.Tests.AutoCloseTests</c>; what these add
/// is the half the issue called out as the risky part — that the events are hooked in the right order and
/// that Enter reaches the latch rather than AvaloniaEdit's newline.
/// </summary>
[Collection(UiTestCollection.Name)]
public class AutoCloseWiringTests
{
    private readonly UiTestSession _ui;

    public AutoCloseWiringTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Typing_an_opener_inserts_the_pair_and_leaves_the_caret_inside() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Typing_an_opener_inserts_the_pair_and_leaves_the_caret_inside));
        var editor = Focused(shell);
        editor.Text = "select ";
        editor.CaretOffset = editor.Text.Length;

        shell.Window.KeyTextInput("'");
        shell.Pump();

        Assert.Equal("select ''", editor.Text);
        Assert.Equal(8, editor.CaretOffset);
    });

    [Fact]
    public Task Typing_the_closer_steps_over_it_instead_of_doubling_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Typing_the_closer_steps_over_it_instead_of_doubling_it));
        var editor = Focused(shell);
        editor.Text = "select ";
        editor.CaretOffset = editor.Text.Length;

        shell.Window.KeyTextInput("(");
        shell.Pump();
        Assert.Equal("select ()", editor.Text);

        shell.Window.KeyTextInput(")");
        shell.Pump();

        Assert.Equal("select ()", editor.Text);      // not "select ())"
        Assert.Equal(9, editor.CaretOffset);
    });

    [Fact]
    public Task Enter_jumps_past_the_closer_rather_than_breaking_the_line() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Enter_jumps_past_the_closer_rather_than_breaking_the_line));
        var editor = Focused(shell);
        editor.Text = "select ";
        editor.CaretOffset = editor.Text.Length;

        shell.Window.KeyTextInput("'");
        shell.Pump();
        shell.Window.KeyTextInput("abc");
        shell.Pump();
        Assert.Equal("select 'abc'", editor.Text);
        Assert.Equal(11, editor.CaretOffset);

        Press(shell, Key.Enter, PhysicalKey.Enter);

        Assert.Equal("select 'abc'", editor.Text);   // no newline
        Assert.Equal(12, editor.CaretOffset);        // past the closer
    });

    [Fact]
    public Task Enter_is_an_ordinary_newline_once_the_pair_is_left() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Enter_is_an_ordinary_newline_once_the_pair_is_left));
        var editor = Focused(shell);
        editor.Text = "select ";
        editor.CaretOffset = editor.Text.Length;

        shell.Window.KeyTextInput("'");
        shell.Pump();
        Press(shell, Key.Enter, PhysicalKey.Enter);   // escapes the pair
        shell.Pump();
        var afterEscape = editor.Text;

        Press(shell, Key.Enter, PhysicalKey.Enter);   // and now it is a newline again

        Assert.True(editor.Text.Length > afterEscape.Length,
            "the second Enter should have broken the line, not jumped again");
        Assert.Contains(((char)10).ToString(), editor.Text);
    });

    [Fact]
    public Task Backspace_on_an_empty_pair_takes_both_halves() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Backspace_on_an_empty_pair_takes_both_halves));
        var editor = Focused(shell);
        editor.Text = "select ";
        editor.CaretOffset = editor.Text.Length;

        shell.Window.KeyTextInput("(");
        shell.Pump();
        Assert.Equal("select ()", editor.Text);

        Press(shell, Key.Back, PhysicalKey.Backspace);

        Assert.Equal("select ", editor.Text);
    });

    [Fact]
    public Task Typing_an_opener_over_a_selection_wraps_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Typing_an_opener_over_a_selection_wraps_it));
        var editor = Focused(shell);
        editor.Text = "select abc from t";
        editor.SelectionStart = 7;
        editor.SelectionLength = 3;
        shell.Pump();

        shell.Window.KeyTextInput("(");
        shell.Pump();

        Assert.Equal("select (abc) from t", editor.Text);
    });

    [Fact]
    public Task Nothing_happens_with_the_setting_off() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Nothing_happens_with_the_setting_off));
        shell.Vm.SettingsService.Update(s => s with { AutoCloseBrackets = false });
        var editor = Focused(shell);
        editor.Text = "select ";
        editor.CaretOffset = editor.Text.Length;

        shell.Window.KeyTextInput("'");
        shell.Pump();

        Assert.Equal("select '", editor.Text);
    });

    /// <summary>The marker showing where Enter will land is drawn while a pair is open, and gone once it is
    /// left. Asserted on rendered pixels, since a background renderer leaves no property to read.</summary>
    [Fact]
    public Task The_marker_is_drawn_while_the_pair_is_open() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_marker_is_drawn_while_the_pair_is_open));
        var editor = Focused(shell);
        editor.Text = "select ";
        editor.CaretOffset = editor.Text.Length;
        shell.Pump();
        var closed = FrameCapture.Of(shell.Window).Within(editor.TextArea.TextView, shell.Window);

        shell.Window.KeyTextInput("'");
        shell.Pump();
        var open = FrameCapture.Of(shell.Window).Within(editor.TextArea.TextView, shell.Window);
        Assert.NotEqual(closed, open);

        // Escaping the pair takes the marker with it, so it never lingers over text it no longer describes.
        Press(shell, Key.Enter, PhysicalKey.Enter);
        var escaped = FrameCapture.Of(shell.Window).Within(editor.TextArea.TextView, shell.Window);
        Assert.NotEqual(open, escaped);
    });

    /// <summary>The editor, focused — text input goes to whatever has the keyboard.</summary>
    private static TextEditor Focused(ShellHarness shell)
    {
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        shell.Pump();
        Assert.True(editor.TextArea.IsFocused, "the editor never took focus");
        return editor;
    }

    private static void Press(ShellHarness shell, Key key, PhysicalKey physical)
    {
        shell.Window.KeyPress(key, RawInputModifiers.None, physical, null);
        shell.Window.KeyRelease(key, RawInputModifiers.None, physical, null);
        shell.Pump();
    }
}
