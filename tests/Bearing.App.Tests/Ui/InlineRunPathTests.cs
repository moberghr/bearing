using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Demo;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The real Run path in Inline mode — keystroke → scope → execution → placement → the pane left alone —
/// over the demo provider, which answers without a server and numbers its statements (§4.6).
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineRunPathTests
{
    private readonly UiTestSession _ui;

    public InlineRunPathTests(UiTestSession ui) => _ui = ui;

    private static async Task<(ShellHarness Shell, TextEditor Editor, EditorTabViewModel Tab, ResultView Pane)> Demo(
        string name, ResultsPlacement placement, string text)
    {
        var providers = new DemoProvider(DemoExecutor.Default(sql => StatementSplitter.Split(sql).Select(s => s.Text).ToList()));
        var shell = await ShellHarness.ShowAsync(name, providers);
        await shell.Vm.StartDemoAsync(shell.ProjectDirectory, DemoMode.WelcomeScript);
        shell.Vm.SettingsService.Update(s => s with { ResultsPlacement = placement });
        shell.Pump();
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        editor.Text = text;
        shell.Pump();
        var pane = shell.Window.GetVisualDescendants().OfType<ResultView>().Single();
        return (shell, editor, shell.Vm.Workspace.SelectedTab!, pane);
    }

    /// <summary>Execution is async; wait for the run to land (bounded), then lay out.</summary>
    private static async Task WaitFor(ShellHarness shell, System.Func<bool> done)
    {
        for (var i = 0; i < 150 && !done(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
            shell.Pump();
        }
        shell.Pump();
    }

    private static void PressF5(ShellHarness shell)
    {
        shell.Window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        shell.Window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
    }

    [Fact]
    public Task Run_places_the_result_under_the_statement_and_leaves_the_pane_closed() => _ui.Run(async () =>
    {
        var (shell, editor, tab, pane) = await Demo(nameof(Run_places_the_result_under_the_statement_and_leaves_the_pane_closed),
            ResultsPlacement.Inline, "select id from shop.payment;\n\nselect 2;\n");
        using var _ = shell;
        editor.CaretOffset = 3;

        PressF5(shell);
        await WaitFor(shell, () => tab.InlineZones.Count > 0);

        var zone = Assert.Single(tab.InlineZones);
        Assert.Equal("select id from shop.payment;".Length, zone.Offset);
        Assert.Same(tab.Results, zone.Sets);
        Assert.False(pane.IsVisible);
        Assert.Single(editor.TextArea.TextView.GetVisualDescendants().OfType<InlineResultView>());
    });

    [Fact]
    public Task Run_on_a_selection_places_the_result_at_the_selection_end() => _ui.Run(async () =>
    {
        const string text = "select 1;\nselect 2;\nselect 3;\n";
        var (shell, editor, tab, _) = await Demo(nameof(Run_on_a_selection_places_the_result_at_the_selection_end),
            ResultsPlacement.Inline, text);
        using var __ = shell;
        editor.Select(text.IndexOf("select 2;"), "select 2;\nselect 3;".Length);

        PressF5(shell);
        await WaitFor(shell, () => tab.InlineZones.Count > 0);

        Assert.Equal(text.IndexOf("select 3;") + "select 3;".Length, Assert.Single(tab.InlineZones).Offset);
    });

    [Fact]
    public Task Run_all_gives_each_numbered_statement_its_own_zone() => _ui.Run(async () =>
    {
        const string text = "select 1; select 2;\nselect 3;\n";
        var (shell, editor, tab, pane) = await Demo(nameof(Run_all_gives_each_numbered_statement_its_own_zone),
            ResultsPlacement.Inline, text);
        using var _ = shell;

        await RunAll(shell);
        await WaitFor(shell, () => tab.InlineZones.Count > 0);

        Assert.Equal(3, tab.InlineZones.Count);
        Assert.Equal(3, editor.TextArea.TextView.GetVisualDescendants().OfType<InlineResultView>().Count());
        Assert.False(pane.IsVisible);
    });

    [Fact]
    public Task In_pane_mode_a_run_opens_the_pane_and_draws_nothing_inline() => _ui.Run(async () =>
    {
        var (shell, editor, tab, pane) = await Demo(nameof(In_pane_mode_a_run_opens_the_pane_and_draws_nothing_inline),
            ResultsPlacement.Pane, "select 1;\n");
        using var _ = shell;
        var before = tab.Results;

        await RunAll(shell);
        await WaitFor(shell, () => !ReferenceEquals(before, tab.Results));

        Assert.True(pane.IsVisible);
        // Not even built: a hidden zone's table would be UI-thread work for nothing.
        Assert.Empty(editor.TextArea.TextView.GetVisualDescendants().OfType<InlineResultView>());
        // Placed all the same, so switching to inline shows them where they belong.
        Assert.Single(tab.InlineZones);
    });

    /// <summary>The pane header's inline toggle: results that ran in the pane appear under their statement and
    /// the pane gets out of the way.</summary>
    [Fact]
    public Task The_pane_headers_toggle_moves_the_results_inline() => _ui.Run(async () =>
    {
        var (shell, editor, tab, pane) = await Demo(nameof(The_pane_headers_toggle_moves_the_results_inline),
            ResultsPlacement.Pane, "select 1;\n");
        using var _ = shell;
        var before = tab.Results;
        await RunAll(shell);
        await WaitFor(shell, () => !ReferenceEquals(before, tab.Results));

        var toggle = pane.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Single(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Show results inline, under their statements");
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        shell.Pump();

        Assert.Equal(ResultsPlacement.Inline, shell.Vm.SettingsService.Current.ResultsPlacement);
        Assert.False(pane.IsVisible);
        Assert.Single(editor.TextArea.TextView.GetVisualDescendants().OfType<InlineResultView>(), v => v.IsEffectivelyVisible);
    });

    /// <summary>query.runAll ships unbound (palette only); its handler, as the palette reaches it.</summary>
    private static Task RunAll(ShellHarness shell)
        => (Task)typeof(Bearing.App.Views.MainWindow)
            .GetMethod("RunAllAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(shell.Window, null)!;
}
