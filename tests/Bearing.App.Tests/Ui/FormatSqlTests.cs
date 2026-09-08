using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Format as the user meets it: the keystroke reaching the editor, the selection rule, and what a refusal
/// does. The layout itself is pinned exhaustively and cheaply in <c>Bearing.Sql.Tests</c> against the pure
/// <c>SqlFormat</c> — what these add is the half that only holds with the shell attached (§4.3).
/// </summary>
[Collection(UiTestCollection.Name)]
public class FormatSqlTests
{
    private readonly UiTestSession _ui;

    public FormatSqlTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Ctrl_shift_F_formats_the_whole_buffer() => _ui.Run(async () =>
    {
        // Editor scope resolves on the tunnel path, so this is really "does the gesture survive AvaloniaEdit".
        using var shell = await ShellHarness.ShowAsync(nameof(Ctrl_shift_F_formats_the_whole_buffer));
        var editor = Focused(shell);
        editor.Text = "select id, name from users where id = 1";
        editor.CaretOffset = 0;
        shell.Pump();

        shell.Window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null);
        shell.Pump();

        Assert.Equal(
            """
            SELECT
                id,
                name
            FROM users
            WHERE id = 1
            """,
            editor.Text);
    });

    [Fact]
    public Task A_selection_is_the_only_thing_formatted() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_selection_is_the_only_thing_formatted));
        var editor = Focused(shell);
        editor.Text = "select 1;\nselect a, b from t";
        editor.SelectionStart = 10;                       // the second statement only
        editor.SelectionLength = editor.Text.Length - 10;
        shell.Pump();

        shell.Window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null);
        shell.Pump();

        Assert.StartsWith("select 1;\n", editor.Text);     // untouched, still lower case
        Assert.Contains("SELECT\n    a,\n    b\nFROM t", editor.Text);
    });

    [Fact]
    public Task Sql_that_will_not_parse_is_left_alone_and_said_so() => _ui.Run(async () =>
    {
        // The formatter refusing is a normal outcome in a query editor. What must not happen is a keystroke
        // that silently does nothing.
        using var shell = await ShellHarness.ShowAsync(nameof(Sql_that_will_not_parse_is_left_alone_and_said_so));
        var editor = Focused(shell);
        editor.Text = "select a from";
        shell.Pump();

        shell.Window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null);
        shell.Pump();

        Assert.Equal("select a from", editor.Text);
        Assert.Contains("not formatted", shell.Vm.StatusText, System.StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task Formatting_is_one_undo_step() => _ui.Run(async () =>
    {
        // A reformat that unwinds line by line is not an undo anyone can use.
        using var shell = await ShellHarness.ShowAsync(nameof(Formatting_is_one_undo_step));
        var editor = Focused(shell);
        editor.Text = "select id, name from users where id = 1";
        shell.Pump();

        shell.Window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null);
        shell.Pump();
        Assert.Contains("SELECT", editor.Text, System.StringComparison.Ordinal);

        editor.Undo();
        shell.Pump();

        Assert.Equal("select id, name from users where id = 1", editor.Text);
    });

    [Fact]
    public Task The_context_menus_Format_item_formats() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_context_menus_Format_item_formats));
        var editor = Focused(shell);
        editor.Text = "select a from t";
        shell.Pump();

        var flyout = (MenuFlyout)editor.TextArea.ContextFlyout!;
        flyout.ShowAt(editor.TextArea);
        shell.Pump();
        flyout.Items.OfType<MenuItem>().First(i => (i.Header as string) == "Format SQL")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shell.Pump();

        Assert.Equal("SELECT\n    a\nFROM t", editor.Text);
        flyout.Hide();
    });

    private static TextEditor Focused(ShellHarness shell)
    {
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        shell.Pump();
        Assert.True(editor.TextArea.IsFocused, "the editor never took focus");
        return editor;
    }
}
