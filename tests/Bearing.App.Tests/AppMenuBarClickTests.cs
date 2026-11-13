using System.Collections.Generic;
using Bearing.App.Input;
using Bearing.App.Views;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>A click on a macOS menu-bar command item (<see cref="AppMenuBar.Click"/>): runs only with the
/// main window in front and the command able to run, and moves focus only when it does run.</summary>
public class AppMenuBarClickTests
{
    private static (KeyCommand Command, List<string> Log) Editor(bool canRun)
    {
        var log = new List<string>();
        var command = KeyCommand.Sync(CommandIds.EditorFormat, "Format SQL", KeyScope.Editor, "Editor",
            () => log.Add("run"), canRun: () => canRun);
        return (command, log);
    }

    [Fact]
    public void An_editor_command_focuses_the_editor_then_runs()
    {
        var (command, log) = Editor(canRun: true);

        Assert.True(AppMenuBar.Click(command, windowActive: true, () => log.Add("focus")));

        Assert.Equal(["focus", "run"], log);
    }

    [Fact]
    public void A_command_that_cannot_run_moves_no_focus()
    {
        var (command, log) = Editor(canRun: false);

        Assert.False(AppMenuBar.Click(command, windowActive: true, () => log.Add("focus")));

        Assert.Empty(log);
    }

    [Fact]
    public void Nothing_runs_behind_a_dialog()
    {
        var (command, log) = Editor(canRun: true);

        Assert.False(AppMenuBar.Click(command, windowActive: false, () => log.Add("focus")));

        Assert.Empty(log);
    }

    [Fact]
    public void A_global_command_runs_without_taking_focus()
    {
        var log = new List<string>();
        var command = KeyCommand.Sync(CommandIds.ViewToggleSidePane, "Toggle side pane", KeyScope.Global, "View",
            () => log.Add("run"));

        Assert.True(AppMenuBar.Click(command, windowActive: true, () => log.Add("focus")));

        Assert.Equal(["run"], log);
    }
}
