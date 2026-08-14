using System.IO;
using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The tab title's hover text — the only place a scratch tab's backing file name is visible, since
/// <see cref="EditorTabViewModel.Header"/> hides it by design.
/// </summary>
public class TabHeaderTooltipTests
{
    private static string Join(params string[] parts) => Path.Combine(parts);

    private static EditorTabViewModel Tab(string? projectDirectory, string? scriptPath, bool isScratch = false)
        => new("Scratch 1", scriptPath: scriptPath, isScratch: isScratch) { ProjectDirectory = projectDirectory };

    [Fact]
    public void Path_under_the_project_is_shown_relative()
    {
        var project = Join("home", "u", "proj");
        var tab = Tab(project, Join(project, "scripts", "scratch", "2026-08-13-02.sql"), isScratch: true);

        Assert.Equal(Join("scripts", "scratch", "2026-08-13-02.sql"), tab.HeaderTooltip);
    }

    [Fact]
    public void Path_outside_the_project_keeps_its_absolute_form()
    {
        // GetRelativePath would answer "../../elsewhere/x.sql"; the absolute path reads better.
        var absolute = Path.GetFullPath(Join("elsewhere", "x.sql"));
        var tab = Tab(Path.GetFullPath(Join("home", "u", "proj")), absolute);

        Assert.Equal(absolute, tab.HeaderTooltip);
    }

    [Fact]
    public void No_project_directory_falls_back_to_the_full_path()
    {
        var path = Join("home", "u", "proj", "scripts", "a.sql");
        Assert.Equal(path, Tab(projectDirectory: null, scriptPath: path).HeaderTooltip);
    }

    [Fact]
    public void A_scratch_tab_with_no_file_yet_says_so_rather_than_showing_nothing()
    {
        // A null tooltip renders as no tooltip at all, which reads as "hovering is broken".
        var tab = Tab(Join("home", "u", "proj"), scriptPath: null, isScratch: true);

        Assert.Equal("Not saved to a file yet", tab.HeaderTooltip);
    }

    [Fact]
    public void Tooltip_updates_when_autosave_creates_the_file()
    {
        var project = Join("home", "u", "proj");
        var tab = Tab(project, scriptPath: null, isScratch: true);
        var raised = false;
        tab.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(EditorTabViewModel.HeaderTooltip);

        tab.ScriptPath = Join(project, "scripts", "scratch", "2026-08-13-01.sql");

        Assert.True(raised, "HeaderTooltip must re-notify when ScriptPath changes");
        Assert.Equal(Join("scripts", "scratch", "2026-08-13-01.sql"), tab.HeaderTooltip);
    }
}
