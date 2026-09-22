using System.Text.Json;
using System.Text.Json.Nodes;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Cli.Tools;
using Bearing.Persistence;
using Bearing.Sessions;
using Bearing.Testing;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// The whole path against a live server: a project file on disk, a password in the real OS keychain, the
/// real providers, and the protocol on top. Skips when Postgres is unreachable (§4.2) — and skips when this
/// machine has no keychain, because a stored password with nowhere to live is not a failure of anything
/// under test.
/// <para>
/// The two assertions that matter most here are the ones that could not be made anywhere else:
/// <c>default_transaction_read_only</c> and <c>statement_timeout</c> are read back <i>from the server</i>,
/// which is the only way to know that <see cref="ExternalAccessPolicy.ForExternalHost"/>'s forcing reached
/// the startup packet rather than merely being set on a record. §1.9 is emphatic that this is the
/// difference between a setting and a claim.
/// </para>
/// </summary>
public class LiveExposureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bearing-mcp-tests", Guid.NewGuid().ToString("N"));

    private readonly Guid _connectionId = Guid.NewGuid();
    private ISecretStore? _secrets;
    private ConnectionSessionManager? _sessions;
    private readonly RecordingQueryLog _log = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // The keychain entry is the one thing here that outlives the process, so it is removed whatever
        // happened — a test that leaves credentials on a developer's machine is its own bug.
        if (_secrets is not null)
        {
            try { await _secrets.DeleteAsync(_connectionId, CancellationToken.None); } catch { }
        }

        if (_sessions is not null) await _sessions.DisposeAsync();

        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The real thing: a project on disk, the password in the OS keychain, the real providers. Skips when
    /// Postgres is unreachable (§4.2), and when this machine has no keychain — a stored password with
    /// nowhere to live is not a failure of anything under test.
    /// </summary>
    private async Task<BearingHost> LiveHostAsync()
    {
        var provider = new PostgresProvider();
        await using (var probe = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password))
            await PgTestServer.RequireAsync(probe);

        _secrets = await SecretStoreFactory.CreatePlatformStoreAsync();
        Skip.If(_secrets is null or { CanStore: false }, "No OS keychain on this machine to store the password in.");
        await _secrets!.SetPasswordAsync(_connectionId, PgTestServer.Password, CancellationToken.None);

        var projectDirectory = await WriteProjectAsync();

        var providers = new ProviderRegistry();
        var credentials = new CredentialResolver(() => _secrets, prompt: null, new EntraTokenProvider());
        // No sweep timer: this manager lives for one test, and a background sweep would be a second thing
        // that could dispose the pool while assertions run.
        _sessions = new ConnectionSessionManager(
            providers, () => credentials, idleTimeout: null, clock: null, runSweepTimer: false);

        return new BearingHost(new JsonProjectStore(), projectDirectory, providers, _sessions, _log);
    }

    [SkippableFact]
    public async Task An_exposed_connection_serves_reads_and_refuses_everything_else()
    {
        var host = await LiveHostAsync();

        // ---- it is listed, and named, and nothing else about it is ---------------------------------
        var listed = Json(await host.ListConnectionsAsync(CancellationToken.None));
        var connection = Assert.Single((JsonArray)listed["connections"]!)!;
        Assert.Equal("agent-reads", connection["name"]!.GetValue<string>());
        Assert.Equal("read-only", connection["access"]!.GetValue<string>());
        Assert.True(connection["available"]!.GetValue<bool>());

        // §1.11: the caller gets a handle, not an address. The key set is the assertion that holds — a
        // field added later has to be argued for here rather than silently carried — and the values are
        // checked too, because a field could carry an address under an innocent name.
        //
        // The connections are deliberately not named after the database: the first version of this called
        // one "agent-pagila", and "no database name in the output" then failed on the connection's own
        // name. A fixture that cannot express the claim is worse than no test (§4.7).
        Assert.Equal(
            ["name", "engine", "access", "available"],
            connection.AsObject().Select(kv => kv.Key));

        var rendered = listed.ToJsonString();
        Assert.DoesNotContain(PgTestServer.Host, rendered);
        Assert.DoesNotContain(PgTestServer.User, rendered);
        Assert.DoesNotContain(PgTestServer.Database, rendered);
        Assert.DoesNotContain(PgTestServer.Password, rendered);
        Assert.DoesNotContain(PgTestServer.Port.ToString(), rendered);

        // ---- the forced settings reached the server, not just the record ---------------------------
        var readOnly = await ScalarAsync(host, "show default_transaction_read_only");
        Assert.Equal("on", readOnly);

        var timeout = await ScalarAsync(host, "show statement_timeout");
        Assert.Equal($"{SessionPolicy.PresetTimeoutSeconds}s", timeout);

        // ---- reads work ----------------------------------------------------------------------------
        var tables = Json(await host.ListTablesAsync("agent-reads", "public", CancellationToken.None));
        var names = ((JsonArray)tables["tables"]!).Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("payment", names);
        Assert.Contains("customer", names);

        var payment = Json(await host.DescribeTableAsync("agent-reads", "public.payment", CancellationToken.None));
        var columns = ((JsonArray)payment["columns"]!).Select(c => c!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("payment_id", columns);

        // pagila's payment is a *partitioned parent* and declares no foreign keys of its own — the three
        // are on each monthly partition. Measured, after this test first asserted the opposite: `select
        // conname from pg_constraint where conrelid='public.payment'::regclass and contype='f'` returns
        // nothing, and relkind is 'p'. Asserted rather than dropped, because it is the shape most likely to
        // be "fixed" by someone who assumed the read was broken (§4.7).
        Assert.Empty((JsonArray)payment["foreign_keys"]!);

        // So the keys are checked on an ordinary table — and on both of its sides, which is what
        // "foreign keys touching it" means and the reason a model can ask "what references this".
        //
        // The arithmetic is measured, not assumed: rental declares three (to customer, inventory and
        // staff) and six of payment's monthly partitions reference it, so nine reach this list. Asserting
        // three here was the third wrong premise in this file, and the count is kept split so a future
        // reader can see which half moved.
        var rental = Json(await host.DescribeTableAsync("agent-reads", "rental", CancellationToken.None));
        var keys = (JsonArray)rental["foreign_keys"]!;

        var declared = keys.Where(k => k!["from"]!["table"]!.GetValue<string>() == "public.rental").ToList();
        var pointingAtIt = keys.Where(k => k!["to"]!["table"]!.GetValue<string>() == "public.rental").ToList();
        Assert.Equal(3, declared.Count);
        Assert.Equal(6, pointingAtIt.Count);
        Assert.Equal(keys.Count, declared.Count + pointingAtIt.Count);

        var toCustomer = Assert.Single(declared, k => k!["to"]!["table"]!.GetValue<string>() == "public.customer")!;
        Assert.Equal("customer_id", toCustomer["from"]!["columns"]![0]!.GetValue<string>());
        Assert.Equal("customer_id", toCustomer["to"]!["columns"]![0]!.GetValue<string>());

        var rows = Json(await host.QueryAsync(
            "agent-reads", "select payment_id, amount from payment order by payment_id", 3, CancellationToken.None));
        var set = ((JsonArray)rows["results"]!)[0]!;
        Assert.Equal(3, set["row_count"]!.GetValue<int>());
        Assert.True(set["truncated"]!.GetValue<bool>());

        // ---- writes do not -------------------------------------------------------------------------
        var refused = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            "agent-reads", "update payment set amount = 0", null, CancellationToken.None));
        Assert.Contains("reads only", refused.Message);
        Assert.Contains("UPDATE", refused.Message);

        // The advice has to be advice the caller can act on. WriteRefusal's own sentence ends "Turn
        // read-only off for this connection to write to it", which is written for the person at the
        // keyboard and is wrong through here twice over: read-only is not this connection's setting, and
        // turning it off would change nothing because exposure forces it back on (§1.1).
        Assert.DoesNotContain("Turn read-only off", refused.Message);

        // The half a client-side refusal cannot provide, and the reason §1.9a insists the two engines are
        // described differently. `nextval` writes, but its statement leads with SELECT, so WriteGuard does
        // not flag it — this reaches the server and the server is what refuses it. The message is Postgres'
        // own, which is the proof: nothing in Bearing produces that sentence.
        //
        // film_film_id_seq is pagila's own (§4.7 — a fixture's contents are a fact to look up, and this one
        // is looked up rather than assumed).
        var atServer = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            "agent-reads", "select nextval('film_film_id_seq')", null, CancellationToken.None));
        Assert.Contains("read-only transaction", atServer.Message);
    }

    /// <summary>
    /// The bypass, against the live server that demonstrated it. Before <c>ExternalSqlPolicy</c> existed
    /// both of these returned a number: <c>begin read write</c> and <c>set transaction read write</c> are
    /// not risky verbs, so the deny-list passed them, and Postgres honoured them — the sequence really
    /// advanced on a connection exposed read-only.
    /// <para>
    /// The sequence is read before and after, because "the command failed" is a weaker claim than "nothing
    /// happened": a refusal that arrived after the statement ran would look identical from the exit code.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_read_only_barrier_cannot_be_lifted_from_outside()
    {
        var host = await LiveHostAsync();

        var before = await ScalarAsync(host, "select last_value from film_film_id_seq");

        foreach (var attempt in new[]
                 {
                     "begin read write; select nextval('film_film_id_seq')",
                     "set transaction read write; select nextval('film_film_id_seq')",
                     "begin; select nextval('film_film_id_seq'); commit",
                     "set default_transaction_read_only = off; select nextval('film_film_id_seq')",
                 })
        {
            var refused = await Assert.ThrowsAsync<CommandFailure>(
                () => host.QueryAsync("agent-reads", attempt, null, CancellationToken.None));
            Assert.NotEmpty(refused.Message);
        }

        Assert.Equal(before, await ScalarAsync(host, "select last_value from film_film_id_seq"));
    }

    /// <summary>
    /// Read-only bounds writes and never bounded these: measured on this same superuser connection with
    /// read-only fully in force, <c>pg_read_file('/etc/passwd')</c> returned the file and
    /// <c>pg_ls_dir('/etc')</c> listed the directory. They are reads — just not of the data.
    /// </summary>
    [SkippableFact]
    public async Task A_function_that_reaches_past_the_data_does_not_run()
    {
        var host = await LiveHostAsync();

        foreach (var attempt in new[]
                 {
                     "select pg_read_file('/etc/passwd')",
                     "select pg_ls_dir('/etc')",
                     "select set_config('default_transaction_read_only', 'off', false)",
                 })
        {
            await Assert.ThrowsAsync<CommandFailure>(
                () => host.QueryAsync("agent-reads", attempt, null, CancellationToken.None));
        }

        // And an ordinary read on the same connection still works, so the refusals above are the policy
        // rather than a connection that stopped functioning.
        Assert.Equal("1000", await ScalarAsync(host, "select count(*)::text from film"));
    }

    /// <summary>
    /// What an agent ran has to be in the record, or the audit export's "what ran against production last
    /// week" is quietly incomplete (§1.6). The origin is what tells such a row from one the user ran.
    /// </summary>
    [SkippableFact]
    public async Task A_query_is_recorded_with_the_origin_that_says_who_ran_it()
    {
        var host = await LiveHostAsync();

        await host.QueryAsync("agent-reads", "select 1", null, CancellationToken.None);

        var row = Assert.Single(_log.Entries);
        Assert.Equal(QueryOrigin.Cli, row.Origin);
        Assert.Equal("select 1", row.SqlText);
        Assert.Equal("agent-reads", row.ConnectionName);
        Assert.Equal(_connectionId, row.ConnectionId);
        Assert.True(row.Success);
    }

    /// <summary>
    /// **A refusal is recorded too**, though nothing ran. §1.3 calls the log the record of SQL the user
    /// ran, and strictly this did not — but the reason this path exists is that the caller is not the
    /// person at the keyboard, and "an agent tried to lift read-only and was stopped" is the single most
    /// useful line the log can hold. Written in the shape a failed execution already has.
    /// </summary>
    [SkippableFact]
    public async Task A_refused_statement_is_recorded_as_an_attempt()
    {
        var host = await LiveHostAsync();

        await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            "agent-reads", "begin read write; select nextval('film_film_id_seq')", null, CancellationToken.None));

        var row = Assert.Single(_log.Entries);
        Assert.Equal(QueryOrigin.Cli, row.Origin);
        Assert.False(row.Success);
        Assert.Contains("BEGIN", row.ErrorMessage);
        // The statement it tried, verbatim — an audit that recorded only "refused" could not say what of.
        Assert.Contains("begin read write", row.SqlText);
        Assert.Equal(0, row.RowCount);
    }

    /// <summary>A connection the user has <b>not</b> exposed is refused by name, and never connected to.</summary>
    [SkippableFact]
    public async Task A_connection_that_was_not_exposed_is_refused_by_name()
    {
        var provider = new PostgresProvider();
        await using var probe = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(probe);

        var projectDirectory = await WriteProjectAsync();

        var providers = new ProviderRegistry();
        await using var sessions = new ConnectionSessionManager(
            providers, () => null, idleTimeout: null, clock: null, runSweepTimer: false);
        var host = new BearingHost(new JsonProjectStore(), projectDirectory, providers, sessions);

        // Only the exposed one is listed at all.
        var listed = Json(await host.ListConnectionsAsync(CancellationToken.None));
        Assert.Single((JsonArray)listed["connections"]!);

        var refused = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync("private-reads", "select 1", null, CancellationToken.None));

        // Told plainly rather than reported as missing: a caller that can run this process can read the
        // same project.json, so hiding it would be theatre — and this way it can ask the owner.
        Assert.Contains("not exposed", refused.Message);
    }

    /// <summary>A project with one exposed connection and one that is not.</summary>
    private async Task<string> WriteProjectAsync()
    {
        var directory = Path.Combine(_root, "project");
        var store = new JsonProjectStore();
        var project = await store.CreateAsync(directory, "Agent", CancellationToken.None);

        project.Manifest.Connections.Add(new ConnectionInfo
        {
            Id = _connectionId,
            Name = "agent-reads",
            ProviderId = PostgresProvider.ProviderId,
            Host = PgTestServer.Host,
            Port = PgTestServer.Port,
            Database = PgTestServer.Database,
            User = PgTestServer.User,
            ExternalAccess = ExternalAccess.ReadOnly,
        });

        project.Manifest.Connections.Add(new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "private-reads",
            ProviderId = PostgresProvider.ProviderId,
            Host = PgTestServer.Host,
            Port = PgTestServer.Port,
            Database = PgTestServer.Database,
            User = PgTestServer.User,
        });

        await store.SaveAsync(project, CancellationToken.None);
        return directory;
    }

    /// <summary>
    /// The first cell, as text. A string comes back unquoted and anything else as its JSON — because
    /// <c>ResultJson</c> renders a bigint as a JSON <i>number</i> (which is the point of it), and a helper
    /// that assumed every scalar was a string threw on the first sequence value it was handed.
    /// </summary>
    private static async Task<string?> ScalarAsync(BearingHost host, string sql)
    {
        var json = Json(await host.QueryAsync("agent-reads", sql, null, CancellationToken.None));
        var cell = ((JsonArray)((JsonArray)json["results"]!)[0]!["rows"]!)[0]![0];

        return cell is null ? null
            : cell.GetValueKind() == JsonValueKind.String ? cell.GetValue<string>()
            : cell.ToJsonString();
    }

    private static JsonObject Json(JsonNode node) => (JsonObject)node;
}
