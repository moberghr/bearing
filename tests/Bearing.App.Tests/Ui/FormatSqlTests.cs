using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Editing;
using Bearing.Core.Workspace;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Format as the user meets it. The layout itself is pinned exhaustively and cheaply in
/// <c>Bearing.Sql.Tests</c> against the pure <c>SqlFormat</c>; what these add is the editor half — the
/// selection rule, the line endings, the undo step, and what a refusal does.
/// <para>
/// Split deliberately, per §4.5. The command is <b>async</b> now (a large script would otherwise freeze the
/// window), and an async command's completion is not reliably observable from a synthetic keystroke — the
/// press returns long before the format lands. So the outcome is asserted against
/// <see cref="EditorTextCommands.FormatSqlAsync"/>, which can be awaited, and the keystroke is left to
/// assert the one thing a press really settles: that the gesture is claimed and routed.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class FormatSqlTests
{
    private readonly UiTestSession _ui;

    public FormatSqlTests(UiTestSession ui) => _ui = ui;

    /// <summary>An editor and the commands over it. No window and no focus: formatting only reads and
    /// rewrites the document, so the parts that need a realized, focused shell are not in play.</summary>
    private static (TextEditor Editor, EditorTextCommands Commands) Editor(string text)
    {
        var editor = new TextEditor { Text = text };
        return (editor, new EditorTextCommands(editor));
    }

    private static SqlFormatOptions Options(
        SqlKeywordCase keywordCase = SqlKeywordCase.Upper, int indent = 4)
        => new() { KeywordCase = keywordCase, IndentWidth = indent };

    // ---- the outcome, awaited ----------------------------------------------------------------------

    [Fact]
    public Task It_formats_the_whole_buffer_when_nothing_is_selected() => _ui.Run(async () =>
    {
        var (editor, commands) = Editor("select id, name from users where id = 1");

        Assert.Null(await commands.FormatSqlAsync(Options()));

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
        var (editor, commands) = Editor("select 1;\nselect a, b from t");
        editor.SelectionStart = 10;                       // the second statement only
        editor.SelectionLength = editor.Text.Length - 10;

        Assert.Null(await commands.FormatSqlAsync(Options()));

        Assert.StartsWith("select 1;\n", editor.Text);     // untouched, still lower case
        Assert.Contains("SELECT\n    a,\n    b\nFROM t", editor.Text);
    });

    [Fact]
    public Task Sql_that_will_not_parse_is_left_alone_and_said_so() => _ui.Run(async () =>
    {
        // A refusal is a normal outcome in a query editor. What must not happen is a keystroke that
        // silently does nothing.
        var (editor, commands) = Editor("select a from");

        var message = await commands.FormatSqlAsync(Options());

        Assert.Equal("select a from", editor.Text);
        Assert.NotNull(message);
        Assert.Contains("not formatted", message, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task Formatting_is_one_undo_step() => _ui.Run(async () =>
    {
        // A reformat that unwinds line by line is not an undo anyone can use.
        var (editor, commands) = Editor("select id, name from users where id = 1");

        await commands.FormatSqlAsync(Options());
        Assert.Contains("SELECT", editor.Text, StringComparison.Ordinal);

        editor.Undo();

        Assert.Equal("select id, name from users where id = 1", editor.Text);
    });

    [Theory]
    [InlineData(SqlKeywordCase.Upper, "SELECT\n    a\nFROM t")]
    [InlineData(SqlKeywordCase.Lower, "select\n    a\nfrom t")]
    [InlineData(SqlKeywordCase.Preserve, "SeLeCt\n    a\nfrOM t")]
    public Task It_follows_the_keyword_case_setting(SqlKeywordCase keywordCase, string expected)
        => _ui.Run(async () =>
        {
            var (editor, commands) = Editor("SeLeCt a frOM t");
            await commands.FormatSqlAsync(Options(keywordCase));
            Assert.Equal(expected, editor.Text);
        });

    [Fact]
    public Task It_follows_the_indent_width_setting() => _ui.Run(async () =>
    {
        var (editor, commands) = Editor("select a from t");
        await commands.FormatSqlAsync(Options(indent: 2));
        Assert.Equal("SELECT\n  a\nFROM t", editor.Text);
    });

    /// <summary>
    /// A one-line selection out of a CRLF document still comes back CRLF. The formatter takes line endings
    /// from the text it is given, and a single-line fragment has none in it to find — so without the editor
    /// passing the document's own convention, formatting a selection spliced bare LFs into a CRLF buffer.
    /// Which is the whole-file diff the detection exists to prevent.
    /// </summary>
    [Fact]
    public Task Formatting_a_selection_keeps_the_documents_line_endings() => _ui.Run(async () =>
    {
        var (editor, commands) = Editor("select 1;\r\nselect id, name from users where id = 1");
        editor.SelectionStart = 11;                       // the second statement, all on one line
        editor.SelectionLength = editor.Text.Length - 11;

        await commands.FormatSqlAsync(Options());

        Assert.Contains("SELECT\r\n    id,\r\n    name\r\nFROM users", editor.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n    id", editor.Text.Replace("\r\n", ""), StringComparison.Ordinal);
    });

    /// <summary>
    /// The format runs off the UI thread, so the user can type while it is away — and an edit computed
    /// against text that has since moved would corrupt the buffer, because the offsets no longer mean what
    /// they meant. It declines and says so rather than writing the stale result.
    /// </summary>
    [Fact]
    public Task An_edit_arriving_mid_format_makes_it_decline_rather_than_write_a_stale_result()
        => _ui.Run(async () =>
        {
            var (editor, commands) = Editor("select id, name from users where id = 1");

            var formatting = commands.FormatSqlAsync(Options());
            editor.Document.Insert(0, "-- typed while it was working\n");   // the user keeps going
            var message = await formatting;

            Assert.NotNull(message);
            Assert.Contains("changed while", message, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("-- typed while it was working", editor.Text, StringComparison.Ordinal);
            Assert.Contains("select id, name from users", editor.Text, StringComparison.Ordinal);
        });

    // ---- the routing, through the shell ------------------------------------------------------------

    /// <summary>
    /// The half a keystroke really settles: Ctrl+Shift+F is claimed by the editor scope rather than falling
    /// through to AvaloniaEdit. Whether the reformat has landed by the time the press returns is not
    /// something a synthetic press can say (§4.5) — that is what the awaited tests above are for.
    /// </summary>
    [Fact]
    public Task Ctrl_shift_F_is_claimed_by_the_editor() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Ctrl_shift_F_is_claimed_by_the_editor));
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        shell.Pump();
        Assert.True(editor.TextArea.IsFocused, "the editor never took focus");

        editor.Text = "select a from t";
        shell.Pump();

        var handled = false;
        shell.Window.AddHandler(InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) => handled = e.Handled,
            RoutingStrategies.Bubble, handledEventsToo: true);

        shell.Window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null);
        shell.Pump();

        Assert.True(handled, "Ctrl+Shift+F was not claimed — it would fall through to the editor");
    });

    /// <summary>The context menu's Format item exists and is wired to the same command; what the command
    /// then does is the awaited tests' business.</summary>
    [Fact]
    public Task The_context_menu_offers_Format() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_context_menu_offers_Format));
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        var flyout = (MenuFlyout)editor.TextArea.ContextFlyout!;

        flyout.ShowAt(editor.TextArea);
        shell.Pump();

        var item = flyout.Items.OfType<MenuItem>().FirstOrDefault(i => (i.Header as string) == "Format SQL");
        Assert.NotNull(item);
        Assert.Equal(KeyGesture.Parse("Ctrl+Shift+F"), item!.InputGesture);

        flyout.Hide();
    });
}
