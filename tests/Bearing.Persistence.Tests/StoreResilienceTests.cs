using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;
using Xunit.Sdk;

namespace Bearing.Persistence.Tests;

/// <summary>
/// Local-store failure modes that used to escalate further than they should: a corrupt session cache stopping
/// a project from opening, a recent-projects list that could only grow, and a keyring delete that reported
/// success without checking.
/// </summary>
public class StoreResilienceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-resilience", Guid.NewGuid().ToString("N"));

    public StoreResilienceTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Theory]
    [InlineData("{ this is not json")]                        // truncated mid-write / hand-edited
    [InlineData("")]                                          // zero-length file
    [InlineData("{\"openEditors\": \"not-an-array\"}")]       // real property, wrong JSON type
    [InlineData("{\"sidePaneWidth\": \"wide\"}")]             // real property, unparseable value
    public async Task A_corrupt_session_file_loads_as_no_session_instead_of_throwing(string content)
    {
        var projectDir = Path.Combine(_root, "proj");
        Directory.CreateDirectory(Path.Combine(projectDir, ".bearing"));
        File.WriteAllText(Path.Combine(projectDir, ".bearing", "session.json"), content);

        var store = new JsonSessionStore();

        // Session state is a disposable cache of window/tab layout — a bad file must not stop the project
        // from opening (it used to throw straight out of project load).
        Assert.Null(await store.LoadAsync(projectDir, CancellationToken.None));
    }

    [Fact]
    public async Task A_valid_session_file_still_round_trips()
    {
        var projectDir = Path.Combine(_root, "good");
        var store = new JsonSessionStore();
        await store.SaveAsync(projectDir, new SessionState { SidePaneOpen = false, SidePaneWidth = 321 },
            CancellationToken.None);

        var loaded = await store.LoadAsync(projectDir, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.False(loaded!.SidePaneOpen);
        Assert.Equal(321, loaded.SidePaneWidth);
    }

    [Fact]
    public async Task A_recent_entry_can_be_removed_and_removing_an_absent_one_is_a_no_op()
    {
        var recent = new FileRecentProjects(Path.Combine(_root, "recent.json"));
        await recent.AddAsync(Path.Combine(_root, "one"), CancellationToken.None);
        await recent.AddAsync(Path.Combine(_root, "two"), CancellationToken.None);

        await recent.RemoveAsync(Path.Combine(_root, "one"), CancellationToken.None);
        Assert.Equal(new[] { Path.GetFullPath(Path.Combine(_root, "two")) },
            await recent.ListAsync(CancellationToken.None));

        // Removing something that isn't there leaves the list (and the file) alone.
        await recent.RemoveAsync(Path.Combine(_root, "never-added"), CancellationToken.None);
        Assert.Single(await recent.ListAsync(CancellationToken.None));
    }

    /// <summary>Deleting a keyring secret has to verify, because `secret-tool clear` exits non-zero both for a
    /// real failure and for "nothing matched" (with an empty stderr) — so neither trusting nor ignoring the
    /// exit code is right. Skipped when no keyring is reachable.</summary>
    [SkippableFact]
    public async Task Deleting_a_keyring_secret_succeeds_and_deleting_a_missing_one_does_not_throw()
    {
        Skip.IfNot(await SecretToolSecretStore.IsAvailableAsync(CancellationToken.None),
            "No Secret Service reachable for keyring test.");

        var store = new SecretToolSecretStore();
        var id = Guid.NewGuid();
        await store.SetPasswordAsync(id, "to-be-deleted", CancellationToken.None);
        Assert.Equal("to-be-deleted", await store.GetPasswordAsync(id, CancellationToken.None));

        await store.DeleteAsync(id, CancellationToken.None);
        Assert.Null(await store.GetPasswordAsync(id, CancellationToken.None));

        // Second delete: nothing matches, exit code is 1, and that must not be reported as a failure.
        await store.DeleteAsync(id, CancellationToken.None);
        await store.DeleteAsync(Guid.NewGuid(), CancellationToken.None);
    }
}
