using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.ViewModels;
using System.IO;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The activity panel's row menu (#101) — the only route to Cancel and Terminate in the running app.
/// <para>
/// The view-model tests call those methods directly, so they cannot see a menu that never opens or one that
/// opens against the wrong row. That second failure is the dangerous one: the menu acts on a backend, and a
/// destructive action aimed at a row the user did not point at is the worst outcome this panel has.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ActivityMenuTests
{
    private readonly UiTestSession _ui;

    public ActivityMenuTests(UiTestSession ui) => _ui = ui;

    private static BackendActivity Backend(int pid) => new(
        Pid: pid,
        BackendStart: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
        User: "app",
        Database: "app",
        Application: "psql",
        State: "active",
        WaitEvent: null,
        RunningFor: TimeSpan.FromSeconds(pid),
        StateFor: TimeSpan.FromSeconds(pid),
        Query: $"select {pid}",
        IsOurs: false);

    /// <summary>A context with nothing live in it: these tests never read, they only open a menu.</summary>
    private static WorkspaceContext Context()
    {
        var root = Path.Combine(Path.GetTempPath(), "bearing-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new WorkspaceContext(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(root, "recent.json")),
            new FakeSecretStore());
    }

    /// <summary>A panel with rows, in a plain window — the menu is the subject, not the shell.</summary>
    private static (Window Window, ActivityPanelView View, ActivityPanelViewModel Vm) Show()
    {
        var vm = new ActivityPanelViewModel(Context());
        foreach (var pid in new[] { 101, 202, 303 }) vm.Backends.Add(new BackendRowViewModel(Backend(pid)));

        var view = new ActivityPanelView { DataContext = vm };
        var window = new Window { Width = 300, Height = 500, Content = view };
        window.Show();
        ResultsHarness.Pump(window);
        return (window, view, vm);
    }

    [Fact]
    public Task Right_clicking_a_row_opens_the_menu_on_that_row() => _ui.Run(() =>
    {
        var (window, view, vm) = Show();
        var list = view.FindControl<ListBox>("BackendList")!;
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(3, rows.Count);

        // Something else selected first, so "the menu acts on the selection" cannot pass by accident.
        vm.Selected = vm.Backends[0];
        ResultsHarness.Pump(window);

        var third = rows[2];
        var at = third.TranslatePoint(new Point(third.Bounds.Width / 2, third.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        ResultsHarness.Pump(window);

        // Both halves matter. A menu that does not open leaves Cancel and Terminate unreachable; a menu that
        // opens while the selection still points at another backend aims them at the wrong session.
        Assert.True(list.ContextMenu?.IsOpen, "the row menu did not open on right-click");
        Assert.Equal(303, vm.Selected?.Pid);

        window.Close();
    });
}
