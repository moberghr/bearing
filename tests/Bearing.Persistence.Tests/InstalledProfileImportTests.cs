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

    // ---- carrying the saved passwords across (approved 2026-09-12) -------------------------------

    /// <summary>
    /// The copy is the one part of this import that writes anything, and it writes a password — so every
    /// branch is pinned here. The source store is injected: the real one is the machine's own credential
    /// store, which only exists on the platform running the test (§4.2), and reading the user's real
    /// keychain from a unit test would be the wrong shape of test twice over.
    /// </summary>
    [Fact]
    public async Task A_saved_password_is_copied_under_the_id_it_landed_on()
    {
        var sourceId = Guid.NewGuid();
        var localId = Guid.NewGuid();
        var source = new FakeStore { Secrets = { [sourceId] = "hunter2" } };
        var target = new FakeStore();

        var carry = await InstalledProfileImport.CopyPasswordsAsync(
            [(sourceId, localId)], target, source: source);

        Assert.Equal(1, carry.Copied);
        Assert.Equal(0, carry.Failed);
        // Under the *local* id: that is the key the resolver will read, and the two differ whenever the
        // import updated a connection already present.
        Assert.Equal("hunter2", target.Secrets[localId]);
        Assert.False(target.Secrets.ContainsKey(sourceId));
    }

    [Fact]
    public async Task A_connection_with_nothing_saved_is_counted_apart_from_a_failure()
    {
        var withOne = (Source: Guid.NewGuid(), Local: Guid.NewGuid());
        var without = (Source: Guid.NewGuid(), Local: Guid.NewGuid());
        var source = new FakeStore { Secrets = { [withOne.Source] = "s3cret" } };

        var carry = await InstalledProfileImport.CopyPasswordsAsync(
            [withOne, without], new FakeStore(), source: source);

        // "Nothing was saved for it" is a normal shape — a prompt-every-time connection — and reporting it
        // as a failure would send the user looking for a broken keychain.
        Assert.Equal(1, carry.Copied);
        Assert.Equal(1, carry.WithoutPassword);
        Assert.Equal(0, carry.Failed);
        Assert.Null(carry.Refused);
    }

    [Fact]
    public async Task Writing_over_a_password_this_profile_already_had_is_reported()
    {
        var same = (Source: Guid.NewGuid(), Local: Guid.NewGuid());
        var different = (Source: Guid.NewGuid(), Local: Guid.NewGuid());
        var source = new FakeStore { Secrets = { [same.Source] = "identical", [different.Source] = "installed" } };
        var target = new FakeStore { Secrets = { [same.Local] = "identical", [different.Local] = "rotated here" } };

        var carry = await InstalledProfileImport.CopyPasswordsAsync(
            [same, different], target, source: source);

        // Only the one whose value actually changed: the old value is gone and cannot be got back, which is
        // what the user needs told. Re-writing the identical value changes nothing and is not worth a word.
        Assert.Equal(2, carry.Copied);
        Assert.Equal(1, carry.Replaced);
        Assert.Equal("installed", target.Secrets[different.Local]);
    }

    [Fact]
    public async Task One_unreadable_secret_does_not_cost_the_others()
    {
        var bad = (Source: Guid.NewGuid(), Local: Guid.NewGuid());
        var good = (Source: Guid.NewGuid(), Local: Guid.NewGuid());
        var source = new FakeStore
        {
            Secrets = { [bad.Source] = "x", [good.Source] = "y" },
            GetThrowsFor = bad.Source,
        };

        var carry = await InstalledProfileImport.CopyPasswordsAsync(
            [bad, good], new FakeStore(), source: source);

        // Best-effort per connection, as one unreadable manifest is in ReadAsync — and the count travels
        // with the reason, because "some could not be copied" over a dozen is a different fact from one.
        Assert.Equal(1, carry.Copied);
        Assert.Equal(1, carry.Failed);
        Assert.NotNull(carry.Refused);
    }

    [Fact]
    public async Task A_session_with_no_keychain_says_so_once_rather_than_failing_per_connection()
    {
        var source = new FakeStore { Secrets = { [Guid.NewGuid()] = "x" } };

        var carry = await InstalledProfileImport.CopyPasswordsAsync(
            [(Guid.NewGuid(), Guid.NewGuid()), (Guid.NewGuid(), Guid.NewGuid())],
            new FakeStore { CanStore = false }, source: source);

        // NoSecretStore throws on every write (§1.1). Two identical failures is noise; the posture is one
        // fact about the session.
        Assert.Equal(0, carry.Copied);
        Assert.Equal(0, carry.Failed);
        Assert.NotNull(carry.Refused);
    }

    /// <summary>A credential store that keeps what it is given, and can be told to misbehave.</summary>
    private sealed class FakeStore : ISecretStore
    {
        public System.Collections.Generic.Dictionary<Guid, string> Secrets { get; } = new();

        /// <summary>Model a keyring that errors rather than answering "no such item" for one connection.</summary>
        public Guid? GetThrowsFor { get; init; }

        public bool IsSecure => true;
        public bool CanStore { get; init; } = true;

        public Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
        {
            if (!CanStore) throw new SecretStorageRefusedException("no keyring (fake)");
            Secrets[connectionId] = password;
            return Task.CompletedTask;
        }

        public Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
            => connectionId == GetThrowsFor
                ? throw new InvalidOperationException("the keyring refused (fake)")
                : Task.FromResult(Secrets.TryGetValue(connectionId, out var v) ? v : null);

        public Task DeleteAsync(Guid connectionId, CancellationToken ct)
        {
            Secrets.Remove(connectionId);
            return Task.CompletedTask;
        }
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
