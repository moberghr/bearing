using Squirrel.Core.Data;
using Squirrel.Core.Workspace;
using Squirrel.Persistence;
using Xunit;

namespace Squirrel.Persistence.Tests;

public class WorkspacePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "squirrel-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Project_round_trips_manifest_and_creates_scripts_dir()
    {
        var dir = Path.Combine(_root, "proj");
        var store = new JsonProjectStore();

        var created = await store.CreateAsync(dir, "Analytics", CancellationToken.None);
        created.Manifest.Connections.Add(new ConnectionInfo
        {
            Id = Guid.NewGuid(), Name = "prod", ProviderId = "postgres",
            Host = "db.internal", Port = 5432, Database = "analytics", User = "reader",
            Options = new Dictionary<string, string> { ["sslmode"] = "require", ["search_path"] = "public,reporting" },
        });
        await store.SaveAsync(created, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(dir, "scripts")));

        var manifestText = await File.ReadAllTextAsync(Path.Combine(dir, "project.json"));
        Assert.DoesNotContain("password", manifestText, StringComparison.OrdinalIgnoreCase); // never persisted

        var reopened = await store.OpenAsync(dir, CancellationToken.None);
        Assert.Equal("Analytics", reopened.Manifest.Name);
        var conn = Assert.Single(reopened.Manifest.Connections);
        Assert.Equal("prod", conn.Name);
        Assert.Equal("analytics", conn.Database);
        Assert.Equal("require", conn.Options["sslmode"]);
    }

    [Fact]
    public async Task Session_round_trips_open_editors_and_active_connection()
    {
        var dir = Path.Combine(_root, "proj2");
        Directory.CreateDirectory(dir);
        var store = new JsonSessionStore();
        var connId = Guid.NewGuid();

        Assert.Null(await store.LoadAsync(dir, CancellationToken.None)); // nothing yet

        var state = new SessionState
        {
            ActiveConnectionId = connId,
            OpenEditors =
            {
                new OpenEditor { ScriptPath = "scripts/daily.sql", CaretOffset = 42, ConnectionId = connId },
                new OpenEditor { ScratchText = "select 1", CaretOffset = 8, ConnectionId = connId },
            },
        };
        await store.SaveAsync(dir, state, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dir, ".squirrel", "session.json")));

        var loaded = await store.LoadAsync(dir, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(connId, loaded!.ActiveConnectionId);
        Assert.Equal(2, loaded.OpenEditors.Count);
        Assert.Equal("scripts/daily.sql", loaded.OpenEditors[0].ScriptPath);
        Assert.Equal("select 1", loaded.OpenEditors[1].ScratchText);
    }

    [Fact]
    public async Task Recent_projects_are_most_recent_first_and_deduped()
    {
        var recent = new FileRecentProjects(Path.Combine(_root, "recent.json"));
        await recent.AddAsync("/a/one", CancellationToken.None);
        await recent.AddAsync("/a/two", CancellationToken.None);
        await recent.AddAsync("/a/one", CancellationToken.None); // re-add moves to front

        var list = await recent.ListAsync(CancellationToken.None);
        Assert.Equal(new[] { Path.GetFullPath("/a/one"), Path.GetFullPath("/a/two") }, list);
    }

    [Fact]
    public async Task File_fallback_secret_store_round_trips_and_stays_out_of_project()
    {
        var store = new FileFallbackSecretStore(Path.Combine(_root, "secrets"));
        var id = Guid.NewGuid();

        Assert.False(store.IsSecure);
        Assert.Null(await store.GetPasswordAsync(id, CancellationToken.None));

        await store.SetPasswordAsync(id, "s3cr3t!", CancellationToken.None);
        Assert.Equal("s3cr3t!", await store.GetPasswordAsync(id, CancellationToken.None));

        await store.DeleteAsync(id, CancellationToken.None);
        Assert.Null(await store.GetPasswordAsync(id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Os_keychain_round_trips_when_available()
    {
        Skip.IfNot(await SecretToolSecretStore.IsAvailableAsync(CancellationToken.None),
            "No Secret Service (keyring) reachable.");

        var store = new SecretToolSecretStore();
        var id = Guid.NewGuid();
        try
        {
            Assert.True(store.IsSecure);
            await store.SetPasswordAsync(id, "keychain-pw", CancellationToken.None);
            Assert.Equal("keychain-pw", await store.GetPasswordAsync(id, CancellationToken.None));
        }
        finally
        {
            await store.DeleteAsync(id, CancellationToken.None);
        }
        Assert.Null(await store.GetPasswordAsync(id, CancellationToken.None));
    }
}
