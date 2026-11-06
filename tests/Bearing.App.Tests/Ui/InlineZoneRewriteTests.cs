using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Commands that rewrite the buffer in one replace — Ctrl+/ and Format — must carry the inline zones across it
/// (<see cref="Editing.InlineResultsController.Rewrite"/>): the replace covers text that didn't really change,
/// and an anchor inside it read as deleted, which emptied every zone in the tab. Through the real window, so the
/// command → <c>EditorTextCommands</c> → controller wiring is what's under test.
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineZoneRewriteTests
{
    private readonly UiTestSession _ui;

    public InlineZoneRewriteTests(UiTestSession ui) => _ui = ui;

    /// <summary>A zone under each of the buffer's two statements, as two Runs would leave them.</summary>
    private static void PlaceUnderBoth(ShellHarness shell, EditorTabViewModel tab)
    {
        var spans = StatementSplitter.Split(PostgresDialect.Instance, tab.Text)
            .Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        foreach (var span in spans)
        {
            var scope = InlineResultPlacement.Scope(PostgresDialect.Instance,
                tab.Text[span.TrimmedStart..span.TrimmedEnd], span.TrimmedStart, isWholeBuffer: false);
            tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, [InlineResultsTests.OneSet()]));
        }
        shell.Pump();
        Assert.Equal(2, tab.InlineZones.Count);
    }

    private static int EndOf(TextEditor editor, string statement)
        => editor.Text.IndexOf(statement, System.StringComparison.Ordinal) + statement.Length;

    private static void Press(ShellHarness shell, Key key, PhysicalKey physical, RawInputModifiers modifiers)
    {
        shell.Window.KeyPress(key, modifiers, physical, null);
        shell.Window.KeyRelease(key, modifiers, physical, null);
        shell.Pump();
    }

    [Fact]
    public Task Toggling_a_line_comment_keeps_every_zone_under_its_statement() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Toggling_a_line_comment_keeps_every_zone_under_its_statement));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        PlaceUnderBoth(shell, tab);
        editor.TextArea.Focus();
        editor.CaretOffset = 0;

        Press(shell, Key.K, PhysicalKey.K, RawInputModifiers.Control);   // editor.toggleComment
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.StartsWith("-- select 1;", editor.Text);                   // the command ran
        Assert.Equal(2, tab.InlineZones.Count);
        Assert.Equal(EndOf(editor, "-- select 1;"), tab.InlineZones[0].Offset);
        Assert.Equal(EndOf(editor, "select 2;"), tab.InlineZones[1].Offset);
        Assert.All(tab.InlineZones, z => Assert.False(z.StatementRemoved));
    });

    [Fact]
    public Task Formatting_the_buffer_keeps_every_zone_under_its_statement() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Formatting_the_buffer_keeps_every_zone_under_its_statement));
        const string messy = "select     1;\nselect     2;\n";
        var (editor, tab) = InlineResultsTests.Setup(shell, messy);
        PlaceUnderBoth(shell, tab);
        editor.TextArea.Focus();

        Press(shell, Key.F, PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);   // editor.format
        for (var i = 0; i < 50 && editor.Text == messy; i++)
        {
            await Task.Delay(20);   // the formatter runs off the UI thread
            shell.Pump();
        }
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.NotEqual(messy, editor.Text);                               // the command ran
        Assert.Equal(2, tab.InlineZones.Count);
        var ends = InlineResultPlacement.Scope(PostgresDialect.Instance, editor.Text, 0, isWholeBuffer: true).StatementEnds;
        Assert.Equal(ends, tab.InlineZones.Select(z => z.Offset));
        Assert.All(tab.InlineZones, z => Assert.False(z.StatementRemoved));
    });
}
