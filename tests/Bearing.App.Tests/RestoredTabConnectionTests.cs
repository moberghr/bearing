using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// A restored session's toolbar pills describe the tab that is selected, under the connection it was saved
/// with. Found when the last tab came back selected with the database pill still showing the database of the
/// tab before it, on another connection, and that server's database list.
/// </summary>
public class RestoredTabConnectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-restore-conn", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
        }
    }

    private static readonly ConnectionInfo First =
        new() { Id = Guid.NewGuid(), Name = "first", ProviderId = "postgres", Database = "one" };

    private static readonly ConnectionInfo Second =
        new() { Id = Guid.NewGuid(), Name = "second", ProviderId = "postgres", Database = "two" };

    private async Task<ShellViewModel> ProjectWith(SessionState session)
    {
        var store = new JsonProjectStore();
        var project = await store.CreateAsync(_root, "p", default);
        project.Manifest.Connections.Add(First);
        project.Manifest.Connections.Add(Second);
        await store.SaveAsync(project, default);
        await new JsonSessionStore().SaveAsync(_root, session, default);

        var vm = new ShellViewModel(
            new ProviderRegistry(),   // no providers: the database list read fails, so the pill keeps the tab's own DB
            store,
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            dialogs: new FakeDialogs(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        await vm.InitializeAsync(_root);
        return vm;
    }

    [Fact]
    public async Task The_last_tab_restored_as_selected_shows_its_own_database()
    {
        var vm = await ProjectWith(new SessionState
        {
            ActiveConnectionId = Second.Id,
            OpenEditors =
            [
                new OpenEditor { ScratchText = "-- a", ConnectionId = First.Id },
                new OpenEditor { ScratchText = "-- b", ConnectionId = Second.Id },
            ],
            SelectedEditorIndex = 1,
        });

        Assert.Same(vm.Workspace.Tabs[1], vm.Workspace.SelectedTab);
        Assert.Equal(Second.Id, vm.Connections.SelectedTabConnection?.Id);
        Assert.Equal("two", vm.Connections.SelectedTabDatabase);
        Assert.Equal(["two"], vm.Connections.TabDatabases.ToArray());   // not the first connection's list
    }

    [Fact]
    public async Task Every_restored_tab_keeps_the_connection_it_was_saved_with()
    {
        var vm = await ProjectWith(new SessionState
        {
            ActiveConnectionId = Second.Id,
            OpenEditors =
            [
                new OpenEditor { ScratchText = "-- a", ConnectionId = First.Id },
                new OpenEditor { ScratchText = "-- b", ConnectionId = Second.Id },
                new OpenEditor { ScratchText = "-- c" },   // none saved: the session's active connection
            ],
            SelectedEditorIndex = 0,
        });

        Assert.Equal(
            [First.Id, Second.Id, Second.Id],
            vm.Workspace.Tabs.Select(t => t.ConnectionId!.Value).ToArray());
    }
}
