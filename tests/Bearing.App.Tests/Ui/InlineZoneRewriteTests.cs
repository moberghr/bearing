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
/// Commands that rewrite the buffer — Ctrl+/ and Format — must keep the inline zones under their statements:
/// they edit as little as they can (<see cref="Results.TextHunks"/>), so anchors move with the text through the
/// edit, its undo and its redo, and an anchor a changed hunk did delete is placed by meaning
/// (<see cref="Editing.InlineResultsController.Rewrite"/>). Through the real window, so the command →
/// <c>EditorTextCommands</c> → controller wiring is what's under test.
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

    /// <summary>A one-column set the provider proved came from statement <paramref name="index"/>.</summary>
    private static ResultSetViewModel Numbered(int index) => new(
        new Bearing.Core.Data.QueryResult([new Bearing.Core.Data.ColumnDescriptor("n", "int4", typeof(int))], [[1]], 1,
            System.TimeSpan.FromMilliseconds(1), null, null, false)
        { StatementIndex = index },
        "select 1;", pageable: false);

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

    /// <summary>Ctrl+/ only inserts the <c>-- </c> markers (one undo step), so its undo and redo are small edits
    /// too: every zone's anchor moves with the text through both, none is read as deleted.</summary>
    [Fact]
    public Task Undoing_and_redoing_a_line_comment_keeps_every_zone_under_its_statement() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Undoing_and_redoing_a_line_comment_keeps_every_zone_under_its_statement));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var original = editor.Text;
        PlaceUnderBoth(shell, tab);
        editor.TextArea.Focus();
        editor.CaretOffset = 0;
        Press(shell, Key.K, PhysicalKey.K, RawInputModifiers.Control);   // editor.toggleComment
        Dispatcher.UIThread.RunJobs();
        var commented = editor.Text;
        Assert.StartsWith("-- select 1;", commented);

        Assert.True(editor.Undo());
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.Equal(original, editor.Text);
        Assert.Equal(2, tab.InlineZones.Count);
        Assert.Equal(EndOf(editor, "select 1;"), tab.InlineZones[0].Offset);
        Assert.Equal(EndOf(editor, "select 2;"), tab.InlineZones[1].Offset);
        Assert.All(tab.InlineZones, z => Assert.False(z.StatementRemoved));
        Assert.Equal(2, InlineResultsTests.Zones(editor).Length);

        Assert.True(editor.Redo());
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.Equal(commented, editor.Text);
        Assert.Equal(2, tab.InlineZones.Count);
        Assert.Equal(EndOf(editor, "-- select 1;"), tab.InlineZones[0].Offset);
        Assert.Equal(EndOf(editor, "select 2;"), tab.InlineZones[1].Offset);
        Assert.All(tab.InlineZones, z => Assert.False(z.StatementRemoved));
    });

    /// <summary>Commenting out the statement a zone with edits hangs under: Ctrl+/ only inserts the markers, so
    /// the zone and its edits stay on that statement's line — not dropped, and not moved to line 1.</summary>
    [Fact]
    public Task A_zone_whose_statement_is_commented_out_keeps_its_line() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_zone_whose_statement_is_commented_out_keeps_its_line));
        var (editor, tab) = InlineResultsTests.Setup(shell, "select 1;\nselect 2;\nselect 3;\n");
        var set = InlineZoneGridTests.Editable();
        try
        {
            var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", 10, isWholeBuffer: false);
            tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, [set]));
            set.SetCell(set.Rows[0], 1, "changed");   // a zone with edits outlives its statement
            shell.Pump();
            var zone = Assert.Single(tab.InlineZones);
            Assert.Equal(19, zone.Offset);
            editor.TextArea.Focus();
            editor.SelectionStart = 10;
            editor.SelectionLength = 20;              // lines 2 and 3

            Press(shell, Key.K, PhysicalKey.K, RawInputModifiers.Control);   // editor.toggleComment
            Dispatcher.UIThread.RunJobs();
            shell.Pump();

            Assert.Equal("select 1;\n-- select 2;\n-- select 3;\n", editor.Text);   // the command ran
            Assert.Same(zone, Assert.Single(tab.InlineZones));
            Assert.Equal(2, editor.Document.GetLineByOffset(zone.Offset).LineNumber);
            Assert.Equal(editor.Document.GetLineByNumber(2).EndOffset, zone.Offset);
            Assert.True(set.HasPendingChanges);
        }
        finally
        {
            set.RevertPending();   // let the harness close it: a quit over pending edits opens the real prompt
        }
    });

    /// <summary>Only the undo stack playing a rewrite back counts as its undo: the same old text pasted over the
    /// whole buffer is an ordinary edit, so the zones in it go with the text they hung under (no edits held).</summary>
    [Fact]
    public Task Replacing_the_buffer_with_the_old_text_is_not_taken_for_an_undo() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Replacing_the_buffer_with_the_old_text_is_not_taken_for_an_undo));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var original = editor.Text;
        PlaceUnderBoth(shell, tab);
        editor.TextArea.Focus();
        editor.CaretOffset = 0;
        Press(shell, Key.K, PhysicalKey.K, RawInputModifiers.Control);   // editor.toggleComment
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, tab.InlineZones.Count);

        editor.Document.Replace(0, editor.Document.TextLength, original);   // select-all + paste, not Undo
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.Equal(original, editor.Text);
        Assert.Empty(tab.InlineZones);
    });

    /// <summary>Format, then results placed under the formatted statements (as Run All would), then Ctrl+Z: the
    /// undo is an ordinary edit to the zones' anchors, and the reformat only touched the text around each
    /// statement's end — so every zone comes back under its statement in the original text.</summary>
    [Fact]
    public Task Undoing_a_format_after_placing_results_keeps_every_zone() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Undoing_a_format_after_placing_results_keeps_every_zone));
        const string messy = "select     1;\nselect     2;\n";
        var (editor, tab) = InlineResultsTests.Setup(shell, messy);
        editor.TextArea.Focus();
        Press(shell, Key.F, PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);   // editor.format
        for (var i = 0; i < 50 && editor.Text == messy; i++)
        {
            await Task.Delay(20);   // the formatter runs off the UI thread
            shell.Pump();
        }
        Assert.NotEqual(messy, editor.Text);
        var all = InlineResultPlacement.Scope(PostgresDialect.Instance, editor.Text, 0, isWholeBuffer: true);
        tab.ApplyInlineRun(all, InlineResultPlacement.Assign(all, [Numbered(0), Numbered(1)]));   // Run All, a zone each
        shell.Pump();
        Assert.Equal(2, tab.InlineZones.Count);

        Assert.True(editor.Undo());
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.Equal(messy, editor.Text);
        Assert.Equal(2, tab.InlineZones.Count);
        var ends = InlineResultPlacement.StatementEnds(PostgresDialect.Instance, messy);
        Assert.Equal(ends, tab.InlineZones.Select(z => z.Offset));
        Assert.All(tab.InlineZones, z => Assert.False(z.StatementRemoved));

        Assert.True(editor.Redo());
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
        Assert.Equal(InlineResultPlacement.StatementEnds(PostgresDialect.Instance, editor.Text), tab.InlineZones.Select(z => z.Offset));
    });

    /// <summary>With zones in the buffer, Format's minimal edits carry the caret: it stays in the same statement,
    /// however much the statements above it grew — not at its old numeric offset. (Without zones Format is one
    /// replace, as before inline results, and the caret is clamped instead.)</summary>
    [Fact]
    public Task Formatting_keeps_the_caret_in_its_statement() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Formatting_keeps_the_caret_in_its_statement));
        const string messy = "select a, b, c, d, e from t where a = 1 and b = 2;\nselect     42;\n";
        var (editor, tab) = InlineResultsTests.Setup(shell, messy);
        var first = InlineResultPlacement.Scope(PostgresDialect.Instance, messy[..(messy.IndexOf(';') + 1)], 0, isWholeBuffer: false);
        tab.ApplyInlineRun(first, InlineResultPlacement.Assign(first, [InlineResultsTests.OneSet()]));   // a zone
        shell.Pump();
        editor.TextArea.Focus();
        editor.CaretOffset = messy.IndexOf("42", System.StringComparison.Ordinal) + 1;

        Press(shell, Key.F, PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);   // editor.format
        for (var i = 0; i < 50 && editor.Text == messy; i++)
        {
            await Task.Delay(20);   // the formatter runs off the UI thread
            shell.Pump();
        }

        Assert.NotEqual(messy, editor.Text);
        var statement = StatementSplitter.StatementAt(PostgresDialect.Instance, editor.Text, editor.CaretOffset);
        Assert.NotNull(statement);
        Assert.Contains("42", statement!.Text);
    });

    /// <summary>A zone whose anchor a reformat deleted, and that no statement end or unchanged text accounts for,
    /// is kept (it holds edits) where the hunk that deleted it now starts — still past the statement above, not
    /// at its old offset, which that statement growing has moved inside it.</summary>
    [Fact]
    public Task An_unmappable_zone_lands_where_its_hunk_starts() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(An_unmappable_zone_lands_where_its_hunk_starts));
        const string messy = "select a, b, c, d, e from t where a = 1 and b = 2;\nselect 42;\n";
        var (editor, tab) = InlineResultsTests.Setup(shell, messy);
        var set = InlineZoneGridTests.Editable();
        try
        {
            // A zone hanging inside the second statement's "select" (offset +3), which Format upper-cases.
            var second = messy.IndexOf("select 42", System.StringComparison.Ordinal);
            var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "sel", second, isWholeBuffer: false);
            tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, [set]));
            set.SetCell(set.Rows[0], 1, "changed");   // a zone with edits outlives its text
            shell.Pump();
            var zone = Assert.Single(tab.InlineZones);
            Assert.Equal(second + 3, zone.Offset);
            editor.TextArea.Focus();

            Press(shell, Key.F, PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);   // editor.format
            for (var i = 0; i < 50 && editor.Text == messy; i++)
            {
                await Task.Delay(20);   // the formatter runs off the UI thread
                shell.Pump();
            }
            Dispatcher.UIThread.RunJobs();
            shell.Pump();

            Assert.NotEqual(messy, editor.Text);
            Assert.Same(zone, Assert.Single(tab.InlineZones));
            var firstEnd = editor.Text.IndexOf("2;", System.StringComparison.Ordinal) + 2;   // "b = 2;"
            Assert.True(zone.Offset >= firstEnd, $"zone at {zone.Offset}, statement 1 ends at {firstEnd}: {editor.Text}");
            Assert.True(zone.Offset <= editor.Text.IndexOf("42", System.StringComparison.Ordinal));
        }
        finally
        {
            set.RevertPending();   // let the harness close it
        }
    });

    /// <summary>A reformat too big for the diff to follow statement by statement still keeps every statement end
    /// a hunk boundary, so zones under them stay there through the format, its undo and its redo.</summary>
    [Fact]
    public Task Zones_survive_undo_and_redo_of_a_large_format() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Zones_survive_undo_and_redo_of_a_large_format));
        var messy = string.Concat(Enumerable.Range(0, 300).Select(i => $"select     {i};\n"));
        var (editor, tab) = InlineResultsTests.Setup(shell, messy);
        foreach (var n in new[] { 10, 200 })
        {
            var at = messy.IndexOf($"select     {n};", System.StringComparison.Ordinal);
            var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, $"select     {n};", at, isWholeBuffer: false);
            tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, [InlineResultsTests.OneSet()]));
        }
        shell.Pump();
        Assert.Equal(2, tab.InlineZones.Count);
        editor.TextArea.Focus();
        editor.CaretOffset = 0;

        Press(shell, Key.F, PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);   // editor.format
        for (var i = 0; i < 300 && editor.Text == messy; i++)
        {
            await Task.Delay(20);   // the formatter runs off the UI thread
            shell.Pump();
        }
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
        Assert.NotEqual(messy, editor.Text);
        AssertUnder(editor, tab, 10, 200);

        Assert.True(editor.Undo());
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
        Assert.Equal(messy, editor.Text);
        AssertUnder(editor, tab, 10, 200);

        Assert.True(editor.Redo());
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
        AssertUnder(editor, tab, 10, 200);
    });

    private static void AssertUnder(TextEditor editor, EditorTabViewModel tab, params int[] statements)
    {
        var ends = InlineResultPlacement.StatementEnds(PostgresDialect.Instance, editor.Text);
        Assert.Equal(statements.Select(n => ends[n]), tab.InlineZones.Select(z => z.Offset));
        Assert.All(tab.InlineZones, z => Assert.False(z.StatementRemoved));
    }

    /// <summary>A zone whose text was deleted (its retirement queued) and then a rewrite before that runs: the
    /// rewrite leaves it to the queued retirement — kept once, re-rendered once.</summary>
    [Fact]
    public Task A_zone_already_deleted_is_retired_once_across_a_rewrite() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_zone_already_deleted_is_retired_once_across_a_rewrite));
        var (editor, tab) = InlineResultsTests.Setup(shell, "select 1;\nselect 2;\nselect 3;\n");
        var set = InlineZoneGridTests.Editable();
        try
        {
            InlineZoneGridTests.Place(shell, tab, set);   // under "select 1;"
            set.SetCell(set.Rows[0], 1, "changed");
            var zone = Assert.Single(tab.InlineZones);
            var renders = 0;
            zone.Changed += () => renders++;
            editor.TextArea.Focus();

            editor.Document.Remove(0, "select 1;\n".Length);   // retirement queued, not yet run
            editor.SelectionStart = 0;
            editor.SelectionLength = editor.Document.TextLength;
            // A rewrite before it runs (raised straight on the text area: real input waits behind the queued
            // retirement), changing text on both sides of the zone's stale offset.
            editor.TextArea.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Key = Key.K,
                PhysicalKey = PhysicalKey.K,
                KeyModifiers = KeyModifiers.Control,
            });
            Dispatcher.UIThread.RunJobs();
            shell.Pump();

            Assert.Equal("-- select 2;\n-- select 3;\n", editor.Text);   // the rewrite ran
            Assert.Same(zone, Assert.Single(tab.InlineZones));
            Assert.True(zone.StatementRemoved);
            Assert.Equal(1, renders);
        }
        finally
        {
            set.RevertPending();   // let the harness close it
        }
    });

    /// <summary>The last statement has no ';' and ends in a keyword Format upper-cases: its zone stays at the new
    /// statement end, and Ctrl+Z puts it back at the old one.</summary>
    [Fact]
    public Task A_zone_after_an_upper_cased_last_word_stays_at_the_statement_end() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_zone_after_an_upper_cased_last_word_stays_at_the_statement_end));
        const string text = "select a from t order by a desc";
        var (editor, tab) = InlineResultsTests.Setup(shell, text);
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, text, 0, isWholeBuffer: false);
        tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, [InlineResultsTests.OneSet()]));
        shell.Pump();
        var zone = Assert.Single(tab.InlineZones);
        Assert.Equal(text.Length, zone.Offset);
        editor.TextArea.Focus();

        Press(shell, Key.F, PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);   // editor.format
        for (var i = 0; i < 50 && editor.Text == text; i++)
        {
            await Task.Delay(20);   // the formatter runs off the UI thread
            shell.Pump();
        }
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.EndsWith("DESC", editor.Text.TrimEnd());
        Assert.Same(zone, Assert.Single(tab.InlineZones));
        Assert.Equal(InlineResultPlacement.StatementEnds(PostgresDialect.Instance, editor.Text)[^1], zone.Offset);

        Assert.True(editor.Undo());
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
        Assert.Equal(text, editor.Text);
        Assert.Same(zone, Assert.Single(tab.InlineZones));
        Assert.Equal(text.Length, zone.Offset);
    });

}
