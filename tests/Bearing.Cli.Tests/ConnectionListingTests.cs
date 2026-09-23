using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Cli.Tools;
using Bearing.Persistence;
using Bearing.Sessions;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// <c>connections</c> against a real project file and the real host — no server, because listing never
/// connects and neither does refusing a name. Plain facts rather than the live suite's skippable ones, so the
/// project and not-exposed fields are pinned on every run and not only where a Postgres answers.
/// </summary>
public class ConnectionListingTests : IAsyncLifetime
{
    private const string PrivateHost = "db-private.internal.example";
    private const string PrivateDatabase = "ledger_private";
    private const string PrivateUser = "ledger_owner";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bearing-cli-listing-tests", Guid.NewGuid().ToString("N"));

    private ConnectionSessionManager? _sessions;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_sessions is not null) await _sessions.DisposeAsync();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task The_listing_names_the_project_it_read()
    {
        var (host, directory) = await HostAsync("Payments", Connection("prod", exposed: true));

        var listed = await ListAsync(host);

        Assert.Equal("Payments", listed.Project?.Name);
        Assert.Equal(directory, listed.Project?.Directory);
    }

    [Fact]
    public async Task Unexposed_connections_are_named_in_project_order_and_never_listed_as_connections()
    {
        var (host, _) = await HostAsync(
            "Payments",
            Connection("stage", exposed: false),
            Connection("prod", exposed: true),
            Connection("dev", exposed: false));

        var listed = await ListAsync(host);

        Assert.Equal(["prod"], listed.Connections.Select(c => c.Name));
        Assert.Equal(["stage", "dev"], listed.NotExposed);
    }

    [Fact]
    public async Task With_nothing_exposed_the_names_are_still_there_to_ask_about()
    {
        var (host, _) = await HostAsync("Payments", Connection("stage", exposed: false));

        var listed = await ListAsync(host);

        Assert.Empty(listed.Connections);
        Assert.Equal(["stage"], listed.NotExposed);
    }

    /// <summary>
    /// The name and nothing else (§1.11). Asserted on the serialised output, because that is what reaches a
    /// caller — a field carrying an address under an innocent name would pass an assertion on the record.
    /// </summary>
    [Fact]
    public async Task An_unexposed_connection_is_named_and_nothing_about_it_is_disclosed()
    {
        var (host, _) = await HostAsync(
            "Payments",
            Connection("prod", exposed: true),
            Connection("private", exposed: false) with
            {
                Host = PrivateHost,
                Port = 6543,
                Database = PrivateDatabase,
                User = PrivateUser,
                Environment = "restricted-env",
            });

        var json = CliRunner.Serialize(await host.ListConnectionsAsync(CancellationToken.None));

        Assert.Contains("\"private\"", json);
        Assert.DoesNotContain(PrivateHost, json);
        Assert.DoesNotContain("6543", json);
        Assert.DoesNotContain(PrivateDatabase, json);
        Assert.DoesNotContain(PrivateUser, json);
        Assert.DoesNotContain("restricted-env", json);
    }

    [Fact]
    public async Task With_everything_exposed_the_list_of_unexposed_is_empty_rather_than_absent()
    {
        // Present and empty, so a caller can read "nothing hidden" rather than "field not supported".
        var (host, _) = await HostAsync("Payments", Connection("prod", exposed: true));

        var json = CliRunner.Serialize(await host.ListConnectionsAsync(CancellationToken.None));

        Assert.Contains("\"not_exposed\": []", json);
    }

    // ---- the shared project read, from both callers ------------------------------------------

    [Fact]
    public async Task A_project_that_cannot_be_read_is_one_sentence_whichever_command_asked()
    {
        var (host, directory) = await HostAsync("Payments", Connection("prod", exposed: true));
        await File.WriteAllTextAsync(Path.Combine(directory, JsonProjectStore.ManifestFileName), "{ not json");

        var listing = await Assert.ThrowsAsync<CommandFailure>(
            () => host.ListConnectionsAsync(CancellationToken.None));
        var query = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest("prod", "select 1"), CancellationToken.None));

        Assert.StartsWith("The Bearing project could not be read", listing.Message);
        Assert.StartsWith("The Bearing project could not be read", query.Message);
    }

    [Fact]
    public async Task A_name_the_listing_reports_as_unexposed_is_refused_as_unexposed_not_as_missing()
    {
        // The two halves have to agree: a name offered under not_exposed and then reported as "no such
        // connection" would send a caller in a circle.
        var (host, _) = await HostAsync("Payments", Connection("stage", exposed: false));

        var listed = await ListAsync(host);
        var refused = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest(listed.NotExposed[0], "select 1"), CancellationToken.None));

        Assert.Contains("not exposed", refused.Message);
    }

    [Fact]
    public async Task A_name_in_neither_list_is_reported_as_missing()
    {
        var (host, _) = await HostAsync("Payments", Connection("prod", exposed: true));

        var refused = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest("stage", "select 1"), CancellationToken.None));

        Assert.Contains("no connection called 'stage'", refused.Message);
    }

    // ---- fixtures -----------------------------------------------------------------------------

    private static async Task<ConnectionsResponse> ListAsync(BearingHost host)
        => (ConnectionsResponse)await host.ListConnectionsAsync(CancellationToken.None);

    private static ConnectionInfo Connection(string name, bool exposed) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = PostgresProvider.ProviderId,
        Host = "localhost",
        Port = 5432,
        Database = "app",
        User = "app",
        ExternalAccess = exposed ? ExternalAccess.ReadOnly : ExternalAccess.None,
    };

    private async Task<(BearingHost Host, string Directory)> HostAsync(
        string projectName, params ConnectionInfo[] connections)
    {
        var directory = Path.Combine(_root, "project");
        var store = new JsonProjectStore();
        var project = await store.CreateAsync(directory, projectName, CancellationToken.None);
        project.Manifest.Connections.AddRange(connections);
        await store.SaveAsync(project, CancellationToken.None);

        var providers = new ProviderRegistry();
        _sessions = new ConnectionSessionManager(
            providers, () => null, idleTimeout: null, clock: null, runSweepTimer: false);

        return (new BearingHost(store, directory, providers, _sessions), project.Directory);
    }
}
