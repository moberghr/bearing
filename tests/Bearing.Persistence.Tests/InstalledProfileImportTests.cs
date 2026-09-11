using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.Persistence.Import;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// Copying the installed app's connections into a build running from source.
/// <para>
/// The isolation this leans on is real: <c>BEARING_PROFILE</c> gives a dev build its own config, data and
/// secret namespace, which is what stops running from source touching the user's projects and query log. The
/// import must not be the hole in it.
/// </para>
/// </summary>
public class InstalledProfileImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bearing-profile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static ConnectionInfo Conn(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "postgres",
        Host = "db.internal",
        Database = "app",
        User = "reader",
        Folder = "Aur",
    };

    /// <summary>A project directory holding the given connections, as the installed profile would have.</summary>
    private async Task<string> ProjectAsync(string name, params ConnectionInfo[] connections)
    {
        var directory = Path.Combine(_root, name);
        var store = new JsonProjectStore();
        var project = await store.CreateAsync(directory, name, CancellationToken.None);
        project.Manifest.Connections.AddRange(connections);
        project.Manifest.ConnectionFolders.Add("Aur");
        await store.SaveAsync(project, CancellationToken.None);
        return directory;
    }

    // ---- the gate --------------------------------------------------------------------------------

    [Fact]
    public void An_installation_has_no_other_profile_to_import_from()
    {
        // The whole feature is "this build is not the installed one". Under the default profile there is
        // nothing to copy from, and the menu item must not appear.
        Assert.Equal("bearing", InstalledProfileImport.InstalledProfile);

        // These tests run without BEARING_PROFILE set, so the process is the default profile.
        var expected = !string.Equals(BearingPaths.AppDirName, "bearing", StringComparison.Ordinal);
        Assert.Equal(expected, InstalledProfileImport.IsSeparateProfile);
    }

    // ---- reading ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_profile_with_nothing_set_up_reports_that_rather_than_throwing()
    {
        // A dev machine where the installed app was never run: no config dir, no projects, no crash.
        var found = await InstalledProfileImport.ReadAsync("bearing-does-not-exist-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(found.Connections);
        Assert.Equal(0, found.Projects);
    }

    [Fact]
    public async Task Reading_a_profile_creates_nothing_under_it()
    {
        // The isolation runs one way, and this is the direction that matters: a build from source must not
        // leave so much as an empty directory in the installed profile's namespace.
        var profile = "bearing-untouched-" + Guid.NewGuid().ToString("N");
        var (config, data) = BearingPaths.RootsFor(profile);

        await InstalledProfileImport.ReadAsync(profile);

        Assert.False(Directory.Exists(config), $"the read created {config}");
        Assert.False(Directory.Exists(data), $"the read created {data}");
    }

    [Fact]
    public async Task A_connection_reachable_through_two_projects_is_offered_once()
    {
        // Recent projects overlap — the same connection is commonly in several. Offering it twice would
        // import a duplicate and leave two rows pointing at one server.
        var shared = Conn("prod");
        var first = await ProjectAsync("one", shared, Conn("staging"));
        var second = await ProjectAsync("two", shared);

        var connections = await ReadFromAsync(first, second);

        Assert.Equal(2, connections.Count);
        Assert.Single(connections, c => c.Id == shared.Id);
    }

    [Fact]
    public async Task An_unreadable_project_does_not_cost_the_others()
    {
        // §5.2: one half-written manifest must not make the whole import fail. Nothing is being changed, so
        // there is nothing to leave inconsistent by carrying on.
        var good = await ProjectAsync("good", Conn("prod"));
        var bad = Path.Combine(_root, "bad");
        Directory.CreateDirectory(bad);
        await File.WriteAllTextAsync(Path.Combine(bad, "project.json"), "{ not json");

        var connections = await ReadFromAsync(good, bad);

        Assert.Single(connections);
        Assert.Equal("prod", connections[0].Name);
    }

    [Fact]
    public async Task The_folders_the_connections_live_in_come_too()
    {
        await ProjectAsync("one", Conn("prod"));
        var found = await ReadAllFromAsync(Path.Combine(_root, "one"));

        Assert.Contains("Aur", found.Folders);
    }

    /// <summary>
    /// Read a set of project directories through the real code path, by pointing a scratch profile's
    /// recent-projects list at them.
    /// </summary>
    private async Task<System.Collections.Generic.IReadOnlyList<ConnectionInfo>> ReadFromAsync(
        params string[] directories)
        => (await ReadAllFromAsync(directories)).Connections;

    private async Task<ProfileConnections> ReadAllFromAsync(params string[] directories)
    {
        var profile = "bearing-src-" + Guid.NewGuid().ToString("N");
        var (config, _) = BearingPaths.RootsFor(profile);
        Directory.CreateDirectory(config);
        try
        {
            var recent = new FileRecentProjects(Path.Combine(config, "recent.json"));
            // Added in reverse so the list reads in the order given, which the dedupe depends on.
            foreach (var directory in directories.Reverse())
                await recent.AddAsync(directory, CancellationToken.None);

            return await InstalledProfileImport.ReadAsync(profile);
        }
        finally
        {
            try { Directory.Delete(config, recursive: true); } catch { /* best effort */ }
        }
    }
}
