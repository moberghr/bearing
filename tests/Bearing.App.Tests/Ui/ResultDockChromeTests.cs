using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The results dock's own chrome: the body of a result that has no grid, and the header's close button.
/// Both are claims about a realized visual — "this text can be selected", "this button exists and calls
/// back" — so they belong here rather than in a pure helper test (§4.3).
/// </summary>
[Collection(UiTestCollection.Name)]
public class ResultDockChromeTests
{
    private readonly UiTestSession _ui;

    public ResultDockChromeTests(UiTestSession ui) => _ui = ui;

    private const string ErrorMessage = "invalid input syntax for type json";

    /// <summary>A failed statement: no columns, no rows, an error. What the dock renders as one line of text.</summary>
    private static ResultSetViewModel FailedResult()
        => new(
            new QueryResult([], [], 0, TimeSpan.Zero, null, new QueryError(ErrorMessage, "22P02", null), false),
            "select '{'::json",
            pageable: false);

    /// <summary>The one text control in a non-grid result's body, found by the text it shows.</summary>
    private static TextBlock BodyText(Visual root, string contains)
        => root.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(t => t.Text is { } s && s.Contains(contains, StringComparison.Ordinal) && s.StartsWith("Error:", StringComparison.Ordinal));

    [Fact]
    public Task An_error_message_can_be_selected() => _ui.Run(() =>
    {
        // The report: a query fails, and the message on screen cannot be selected, so it has to be retyped
        // into a search box. A plain TextBlock refuses silently — there is no affordance to notice missing.
        var (window, view) = ResultsHarness.Show(FailedResult());

        var body = BodyText(view, ErrorMessage);
        var selectable = Assert.IsType<SelectableTextBlock>(body);

        selectable.SelectAll();
        Assert.Equal($"Error: {ErrorMessage}", selectable.SelectedText);
        // Ctrl+C is the control's own handler, and it only ever runs on the focused control.
        Assert.True(selectable.Focusable);

        window.Close();
    });

    [Fact]
    public Task Ctrl_C_puts_the_selected_error_on_the_clipboard() => _ui.Run(async () =>
    {
        // Being selectable is only half of it — the copy is the point. SelectableTextBlock handles Ctrl+C
        // itself, but only on the focused control, so this is really a test that focus can land there.
        var (window, view) = ResultsHarness.Show(FailedResult());
        var body = (SelectableTextBlock)BodyText(view, ErrorMessage);

        body.Focus();
        ResultsHarness.Pump(window);
        Assert.True(body.IsFocused, "the error text never took focus");
        body.SelectAll();

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        ResultsHarness.Pump(window);

        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
        Assert.NotNull(clipboard);
        using var transfer = await clipboard!.TryGetDataAsync();
        Assert.NotNull(transfer);
        Assert.Equal($"Error: {ErrorMessage}", await transfer!.TryGetTextAsync());

        window.Close();
    });

    [Fact]
    public Task The_shells_close_button_actually_collapses_the_pane() => _ui.Run(async () =>
    {
        // ResultView only reports the click; the collapse is the shell's lambda, and that half was untested.
        using var shell = await ShellHarness.ShowAsync(nameof(The_shells_close_button_actually_collapses_the_pane));
        var results = shell.Window.GetVisualDescendants().OfType<ResultView>().First();
        var workspace = shell.Window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "WorkspaceGrid");

        // The window opens with the pane collapsed (nothing has run yet). Put it in the state the ✕ is
        // reachable from: a result to draw the header over, and the split open.
        results.Results = [FailedResult()];
        workspace.RowDefinitions[0].Height = new GridLength(2, GridUnitType.Star);
        workspace.RowDefinitions[1].Height = GridLength.Auto;
        workspace.RowDefinitions[2].Height = new GridLength(3, GridUnitType.Star);
        results.IsVisible = true;
        shell.Pump();

        var close = CloseButton(results);
        Assert.NotNull(close);
        close!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.Pump();

        Assert.False(results.IsVisible);
        // The row too, not just the control: that is the pane controller having run rather than the callback
        // merely having fired.
        Assert.Equal(0, workspace.RowDefinitions[2].Height.Value);
        // And focus came back out of the pane that is no longer on screen.
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        Assert.True(editor.TextArea.IsFocused, "focus was left in the collapsed pane");
    });

    [Fact]
    public Task The_dock_header_has_no_close_button_until_the_shell_wires_one() => _ui.Run(() =>
    {
        // A ✕ that closes nothing is a dead control, and ResultView is used bare (here, and in the harness).
        var (window, view) = ResultsHarness.Show(FailedResult());

        Assert.Null(CloseButton(view));

        window.Close();
    });

    [Fact]
    public Task The_dock_headers_close_button_asks_the_shell_to_collapse_the_pane() => _ui.Run(() =>
    {
        var closed = 0;
        var view = new ResultView { CloseRequested = () => closed++ };
        view.Results = [FailedResult()];          // assigned second: this is what renders the header
        var window = new Window { Width = 800, Height = 500, Content = view };
        window.Show();
        ResultsHarness.Pump(window);

        var close = CloseButton(view);
        Assert.NotNull(close);
        close!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, closed);
        window.Close();
    });

    /// <summary>The header's ✕, found by the tooltip the chrome already sets — no test-only hook in the
    /// production visual (§4.5).</summary>
    private static Button? CloseButton(Visual root)
        => root.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => ToolTip.GetTip(b) as string is { } tip
                && tip.StartsWith("Close results", StringComparison.Ordinal));
}
