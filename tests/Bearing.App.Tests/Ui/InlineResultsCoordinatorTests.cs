using System.Threading.Tasks;
using Avalonia.Controls;
using AvaloniaEdit;
using Bearing.App.Editing;
using Bearing.App.Results;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// When the results pane opens for a run (<see cref="InlineResultsCoordinator"/>): in inline mode a run whose
/// results can't be placed under their statement owes the user the pane — now if its tab is shown, else the
/// next time it is. Over a bare editor, with the window's pane and tab switch stood in by delegates.
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineResultsCoordinatorTests
{
    private readonly UiTestSession _ui;

    public InlineResultsCoordinatorTests(UiTestSession ui) => _ui = ui;

    private const string Buffer = "select 1;\nselect 2;\n";

    private sealed class Harness
    {
        public required TextEditor Editor { get; init; }
        public required InlineResultsCoordinator Coordinator { get; init; }
        public EditorTabViewModel? Selected { get; set; }
        public bool PaneVisible { get; set; }

        /// <summary>What the window does on a tab switch: unbind, swap the buffer, bind, render results.</summary>
        public void Select(EditorTabViewModel tab)
        {
            Coordinator.Controller.Unbind();
            Editor.Text = tab.Text;
            Selected = tab;
            Coordinator.Controller.Bind(tab);
            Rebuild(tab);
        }

        public void Rebuild(EditorTabViewModel? tab)
        {
            if (Coordinator.PaneVisibilityFor(tab) is { } visible)
            {
                PaneVisible = visible;
            }
        }
    }

    private static Harness Build()
    {
        var editor = new TextEditor();
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        var settings = SettingsService.InMemory(new AppSettings { ResultsPlacement = ResultsPlacement.Inline });
        Harness? h = null;
        var coordinator = new InlineResultsCoordinator(editor, () => settings, () => h!.Selected,
            tab => h!.Rebuild(tab), visible => h!.PaneVisible = visible, _ => { }, () => { });
        h = new Harness { Editor = editor, Coordinator = coordinator };
        return h;
    }

    private static RunScope SecondStatement()
        => InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", "select 1;\n".Length, isWholeBuffer: false);

    [Fact]
    public Task A_placed_run_leaves_the_pane_closed() => _ui.Run(async () =>
    {
        var h = Build();
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);

        await h.Coordinator.RunAsync(SecondStatement(), _ =>
        {
            tab.SetRunResults([InlineResultsTests.OneSet()]);
            return Task.CompletedTask;
        });

        Assert.Single(tab.InlineZones);
        Assert.False(h.PaneVisible);
    });

    /// <summary>The run finished on a background tab whose text changed under a rebind: it can't be placed, so
    /// switching back must open the pane — otherwise its results would be nowhere on screen.</summary>
    [Fact]
    public Task An_unplaceable_run_that_finished_in_the_background_opens_the_pane_on_return() => _ui.Run(async () =>
    {
        var h = Build();
        var tab = new EditorTabViewModel("t", Buffer);
        var other = new EditorTabViewModel("o", "select 42;");
        h.Select(tab);

        await h.Coordinator.RunAsync(SecondStatement(), _ =>
        {
            tab.Text = Buffer + "-- typed while it ran\n";
            h.Select(other);                       // switched away; the run lands there
            tab.SetRunResults([InlineResultsTests.OneSet()]);
            return Task.CompletedTask;
        });
        Assert.Empty(tab.InlineZones);
        Assert.False(h.PaneVisible);               // not on screen yet: another tab is shown

        h.Select(tab);

        Assert.True(h.PaneVisible);
    });

    /// <summary>The debt is paid once: a later switch back to the tab doesn't reopen a pane the user closed.</summary>
    [Fact]
    public Task The_pane_is_owed_only_once() => _ui.Run(async () =>
    {
        var h = Build();
        var tab = new EditorTabViewModel("t", Buffer);
        var other = new EditorTabViewModel("o", "select 42;");
        h.Select(tab);
        await h.Coordinator.RunAsync(SecondStatement(), _ =>
        {
            tab.Text = Buffer + "-- typed while it ran\n";
            h.Select(other);
            tab.SetRunResults([InlineResultsTests.OneSet()]);
            return Task.CompletedTask;
        });
        h.Select(tab);
        h.PaneVisible = false;                     // the user closes it

        h.Select(other);
        h.Select(tab);

        Assert.False(h.PaneVisible);
    });
}
